using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SkiaSharp;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Infrastructure.Services;

public sealed record ImageUploadResultView(
    Guid Id,
    string Url,
    int Width,
    int Height,
    string MimeType,
    long ByteSize);

public sealed record AvatarUploadResultView(
    string? AvatarUrl,
    string? GitHubAvatarUrl);

public sealed class ImageUploadService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    IMediaStorage storage,
    TimeProvider timeProvider,
    ILogger<ImageUploadService> logger)
{
    public async Task<ImageUploadResultView> UploadAsync(
        Guid actorId,
        Stream file,
        long declaredLength,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireWriteAccessAsync(
            actorId,
            ActorWriteScope.Content,
            cancellationToken);
        if (declaredLength > MediaLimits.MaxImageBytes)
        {
            throw ApiProblemException.UploadTooLarge(MediaLimits.MaxImageBytes);
        }

        var bytes = await ReadFileAsync(file, declaredLength, cancellationToken);
        var decoded = Decode(bytes);
        var now = timeProvider.GetUtcNow();
        var objectKey = MediaStoragePaths.BuildObjectKey(now, decoded.Extension);
        var url = await storage.SaveAsync(
            decoded.Content,
            objectKey,
            decoded.MimeType,
            cancellationToken);

        var upload = new MediaUpload
        {
            Id = Guid.CreateVersion7(),
            OwnerId = actor.Id,
            Url = url,
            Width = decoded.Width,
            Height = decoded.Height,
            MimeType = decoded.MimeType,
            ByteSize = decoded.Content.Length,
            Status = MediaUploadStatuses.Temp,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };

        db.MediaUploads.Add(upload);
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "media.uploaded",
            "media_upload",
            upload.Id,
            new
            {
                mimeType = upload.MimeType,
                width = upload.Width,
                height = upload.Height,
                byteSize = upload.ByteSize,
                storage = storage.ProviderName
            },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await TryDeleteAsync(url, upload.Id);
            throw;
        }

        return new ImageUploadResultView(
            upload.Id,
            upload.Url,
            upload.Width,
            upload.Height,
            upload.MimeType,
            upload.ByteSize);
    }

    public async Task<AvatarUploadResultView> UploadAvatarAsync(
        Guid actorId,
        Stream file,
        long declaredLength,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var bytes = await ReadFileAsync(file, declaredLength, cancellationToken);
        if (bytes.Length > 2 * 1024 * 1024)
        {
            throw ApiProblemException.Validation("file", "头像不能超过 2 MB。");
        }

        var decoded = Decode(bytes);
        var now = timeProvider.GetUtcNow();
        var objectKey = MediaStoragePaths.BuildObjectKey(now, decoded.Extension);
        var url = await storage.SaveAsync(
            decoded.Content,
            objectKey,
            decoded.MimeType,
            cancellationToken);

        var user = await db.Users.SingleAsync(
            candidate => candidate.Id == actor.Id,
            cancellationToken);
        var previousUrl = user.AvatarUrl;
        var previousAvatars = await db.MediaUploads
            .Where(upload => upload.OwnerId == actor.Id
                && upload.Status == MediaUploadStatuses.Avatar
                && previousUrl != null
                && upload.Url == previousUrl)
            .ToListAsync(cancellationToken);
        foreach (var previous in previousAvatars)
        {
            previous.Status = MediaUploadStatuses.Deleted;
            previous.ExpiresAt = now;
        }

        var avatar = new MediaUpload
        {
            OwnerId = actor.Id,
            Url = url,
            Width = decoded.Width,
            Height = decoded.Height,
            MimeType = decoded.MimeType,
            ByteSize = decoded.Content.Length,
            Status = MediaUploadStatuses.Avatar,
            CreatedAt = now,
            ExpiresAt = now.AddYears(100)
        };
        db.MediaUploads.Add(avatar);
        user.AvatarUrl = url;
        user.UpdatedAt = now;
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "user.avatar_updated",
            "user",
            user.Id,
            new
            {
                mimeType = avatar.MimeType,
                width = avatar.Width,
                height = avatar.Height,
                byteSize = avatar.ByteSize
            },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await TryDeleteAsync(url, avatar.Id);
            throw;
        }

        return new AvatarUploadResultView(user.AvatarUrl, user.GitHubAvatarUrl);
    }

    public async Task<AvatarUploadResultView> ClearAvatarAsync(
        Guid actorId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var user = await db.Users.SingleAsync(
            candidate => candidate.Id == actor.Id,
            cancellationToken);
        if (user.AvatarUrl is null)
        {
            return new AvatarUploadResultView(user.AvatarUrl, user.GitHubAvatarUrl);
        }

        var now = timeProvider.GetUtcNow();
        var currentAvatars = await db.MediaUploads
            .Where(upload => upload.OwnerId == actor.Id
                && upload.Status == MediaUploadStatuses.Avatar
                && upload.Url == user.AvatarUrl)
            .ToListAsync(cancellationToken);
        foreach (var current in currentAvatars)
        {
            current.Status = MediaUploadStatuses.Deleted;
            current.ExpiresAt = now;
        }

        user.AvatarUrl = user.GitHubAvatarUrl;
        user.UpdatedAt = now;
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "user.avatar_cleared",
            "user",
            user.Id,
            new { restoredGitHubAvatar = user.GitHubAvatarUrl is not null },
            now));
        await db.SaveChangesAsync(cancellationToken);
        return new AvatarUploadResultView(user.AvatarUrl, user.GitHubAvatarUrl);
    }

    private static async Task<byte[]> ReadFileAsync(
        Stream file,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        var capacity = (int)Math.Clamp(declaredLength, 0, MediaLimits.MaxImageBytes);
        await using var buffer = new MemoryStream(capacity);
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await file.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MediaLimits.MaxImageBytes)
            {
                throw ApiProblemException.UploadTooLarge(MediaLimits.MaxImageBytes);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
        {
            throw ApiProblemException.Validation("file", "请选择非空图片文件。");
        }

        return buffer.ToArray();
    }

    private static DecodedImage Decode(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        SKCodec? codec;
        try
        {
            codec = SKCodec.Create(data);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw ApiProblemException.UploadUnsupportedType();
        }

        if (codec is null)
        {
            throw ApiProblemException.UploadUnsupportedType();
        }

        using (codec)
        {
            var (lossless, supported) = codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Jpeg => (false, true),
                SKEncodedImageFormat.Png => (true, true),
                SKEncodedImageFormat.Gif => (true, true),
                SKEncodedImageFormat.Webp => (false, true),
                _ => (false, false)
            };
            if (!supported)
            {
                throw ApiProblemException.UploadUnsupportedType();
            }

            var width = codec.Info.Width;
            var height = codec.Info.Height;
            var frameCount = Math.Max(1, codec.FrameCount);
            if (width <= 0 || height <= 0
                || width > MediaLimits.MaxImageDimension
                || height > MediaLimits.MaxImageDimension
                || (long)width * height > MediaLimits.MaxImagePixels)
            {
                throw ApiProblemException.Validation(
                    "file",
                    $"图片尺寸过大，最长边需不超过 {MediaLimits.MaxImageDimension} 像素且总像素不超过 {MediaLimits.MaxImagePixels}。");
            }

            if (frameCount > MediaLimits.MaxAnimationFrames)
            {
                throw ApiProblemException.Validation(
                    "file",
                    $"动画图片不能超过 {MediaLimits.MaxAnimationFrames} 帧。");
            }

            if ((long)width * height * frameCount > MediaLimits.MaxAnimationPixels)
            {
                throw ApiProblemException.Validation("file", "图片总像素或动画帧过多，请压缩后重试。");
            }

            SKBitmap? decoded;
            try
            {
                decoded = SKBitmap.Decode(codec);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw ApiProblemException.UploadUnsupportedType();
            }

            if (decoded is null || decoded.Width != width || decoded.Height != height)
            {
                decoded?.Dispose();
                throw ApiProblemException.UploadUnsupportedType();
            }

            using (decoded)
            {
                using var normalized = new SKBitmap(new SKImageInfo(
                    width,
                    height,
                    SKColorType.Bgra8888,
                    SKAlphaType.Premul));
                if (!decoded.CopyTo(normalized, SKColorType.Bgra8888))
                {
                    throw ApiProblemException.UploadUnsupportedType();
                }

                using var oriented = ApplyOrientation(normalized, codec.EncodedOrigin);
                var source = oriented ?? normalized;
                var content = EncodeWebp(source, lossless);
                return new DecodedImage(content, source.Width, source.Height);
            }
        }
    }

    private static byte[] EncodeWebp(SKBitmap bitmap, bool lossless)
    {
        var encoded = EncodeWebpCore(bitmap, lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy);
        if (encoded.Length <= MediaLimits.MaxImageBytes)
        {
            return encoded;
        }

        encoded = EncodeWebpCore(bitmap, SKWebpEncoderCompression.Lossy);
        if (encoded.Length <= MediaLimits.MaxImageBytes)
        {
            return encoded;
        }

        throw ApiProblemException.UploadTooLarge(MediaLimits.MaxImageBytes);
    }

    private static byte[] EncodeWebpCore(SKBitmap bitmap, SKWebpEncoderCompression compression)
    {
        using var pixmap = bitmap.PeekPixels();
        if (pixmap is null)
        {
            throw new InvalidOperationException("Unable to read decoded image pixels.");
        }

        var options = compression == SKWebpEncoderCompression.Lossless
            ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100)
            : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 90);
        using var data = pixmap.Encode(options);
        if (data is null)
        {
            throw new InvalidOperationException("Unable to encode uploaded image.");
        }

        return data.ToArray();
    }

    private static SKBitmap? ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return null;
        }

        var swapsDimensions = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
        var width = swapsDimensions ? source.Height : source.Width;
        var height = swapsDimensions ? source.Width : source.Height;
        var destination = new SKBitmap(new SKImageInfo(
            width,
            height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        try
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (sourceX, sourceY) = origin switch
                    {
                        SKEncodedOrigin.TopRight => (source.Width - 1 - x, y),
                        SKEncodedOrigin.BottomRight => (source.Width - 1 - x, source.Height - 1 - y),
                        SKEncodedOrigin.BottomLeft => (x, source.Height - 1 - y),
                        SKEncodedOrigin.LeftTop => (y, x),
                        SKEncodedOrigin.RightTop => (y, source.Height - 1 - x),
                        SKEncodedOrigin.RightBottom => (source.Width - 1 - y, source.Height - 1 - x),
                        SKEncodedOrigin.LeftBottom => (source.Width - 1 - y, x),
                        _ => (x, y)
                    };
                    destination.SetPixel(x, y, source.GetPixel(sourceX, sourceY));
                }
            }

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    private async Task TryDeleteAsync(string url, Guid uploadId)
    {
        try
        {
            await storage.DeleteAsync(url);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to delete media object for rejected upload {MediaUploadId}.",
                uploadId);
        }
    }

    private sealed record DecodedImage(byte[] Content, int Width, int Height)
    {
        public string MimeType => "image/webp";
        public string Extension => "webp";
    }
}