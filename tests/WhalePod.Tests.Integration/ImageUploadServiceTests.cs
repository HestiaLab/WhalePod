using Microsoft.EntityFrameworkCore;

using SkiaSharp;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Tests.Integration;

public sealed class ImageUploadServiceTests
{
    [DatabaseFact]
    public async Task Avatar_upload_replaces_old_object_and_clear_restores_github_avatar()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        using var media = new TestMediaStorage();

        var member = ServiceTestSupport.NewUser("avatar-member");
        member.GitHubAvatarUrl = "https://avatars.githubusercontent.com/u/42?v=4";
        member.AvatarUrl = member.GitHubAvatarUrl;
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var service = ServiceTestSupport.CreateImageUploadService(db, media.Storage);
        var firstBytes = TestImages.Png(32, 32);
        using var firstStream = new MemoryStream(firstBytes);
        var first = await service.UploadAvatarAsync(
            member.Id,
            firstStream,
            firstBytes.Length,
            ServiceTestSupport.NewAudit(member.Id));
        Assert.Matches(@"^/media/\d{4}/\d{2}/[0-9a-f]{32}\.webp$", first.AvatarUrl);
        Assert.Equal(member.GitHubAvatarUrl, first.GitHubAvatarUrl);

        db.ChangeTracker.Clear();
        var storedUser = await db.Users.SingleAsync(user => user.Id == member.Id);
        Assert.Equal(first.AvatarUrl, storedUser.AvatarUrl);
        var firstUpload = await db.MediaUploads.SingleAsync(upload =>
            upload.OwnerId == member.Id
            && upload.Url == first.AvatarUrl
            && upload.Status == MediaUploadStatuses.Avatar);
        Assert.True(firstUpload.ExpiresAt > DateTimeOffset.UtcNow.AddYears(90));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "user.avatar_updated" && log.ActorId == member.Id));

        var secondBytes = TestImages.Png(48, 48);
        using var secondStream = new MemoryStream(secondBytes);
        var second = await service.UploadAvatarAsync(
            member.Id,
            secondStream,
            secondBytes.Length,
            ServiceTestSupport.NewAudit(member.Id));
        Assert.NotEqual(first.AvatarUrl, second.AvatarUrl);

        db.ChangeTracker.Clear();
        var replaced = await db.MediaUploads.SingleAsync(upload => upload.Url == first.AvatarUrl);
        Assert.Equal(MediaUploadStatuses.Deleted, replaced.Status);
        Assert.True(replaced.ExpiresAt <= DateTimeOffset.UtcNow);
        Assert.True(File.Exists(media.ResolvePath(second.AvatarUrl!)));

        var cleared = await service.ClearAvatarAsync(
            member.Id,
            ServiceTestSupport.NewAudit(member.Id));
        Assert.Equal(member.GitHubAvatarUrl, cleared.AvatarUrl);
        db.ChangeTracker.Clear();
        Assert.Equal(
            MediaUploadStatuses.Deleted,
            (await db.MediaUploads.SingleAsync(upload => upload.Url == second.AvatarUrl)).Status);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "user.avatar_cleared" && log.ActorId == member.Id));
    }

    [DatabaseFact]
    public async Task Upload_sniffs_content_reencodes_webp_and_creates_temporary_upload()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        using var media = new TestMediaStorage();

        var member = ServiceTestSupport.NewUser("upload-member");
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var service = ServiceTestSupport.CreateImageUploadService(db, media.Storage);
        var png = TestImages.Png(24, 12);
        using var stream = new MemoryStream(png);
        var result = await service.UploadAsync(
            member.Id,
            stream,
            png.Length,
            ServiceTestSupport.NewAudit(member.Id));

        Assert.Matches(@"^/media/\d{4}/\d{2}/[0-9a-f]{32}\.webp$", result.Url);
        Assert.Equal("image/webp", result.MimeType);
        Assert.Equal(24, result.Width);
        Assert.Equal(12, result.Height);
        Assert.True(result.ByteSize > 0);
        Assert.True(result.ByteSize <= MediaLimits.MaxImageBytes);
        Assert.True(File.Exists(media.ResolvePath(result.Url)));

        using (var encoded = SKData.Create(media.ResolvePath(result.Url)))
        using (var codec = SKCodec.Create(encoded))
        {
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
            Assert.Equal(24, codec.Info.Width);
            Assert.Equal(12, codec.Info.Height);
        }

        db.ChangeTracker.Clear();
        var upload = await db.MediaUploads.SingleAsync(candidate => candidate.Id == result.Id);
        Assert.Equal(member.Id, upload.OwnerId);
        Assert.Equal(MediaUploadStatuses.Temp, upload.Status);
        Assert.Null(upload.PostId);
        Assert.True(upload.ExpiresAt > DateTimeOffset.UtcNow.AddHours(23));
        Assert.Equal(result.Url, upload.Url);
        Assert.Equal("image/webp", upload.MimeType);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "media.uploaded"
            && log.TargetId == result.Id
            && log.ActorId == member.Id
            && log.IpHash != null));
    }

    [DatabaseFact]
    public async Task Upload_applies_exif_orientation_and_strips_metadata()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        using var media = new TestMediaStorage();

        var member = ServiceTestSupport.NewUser("upload-exif");
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var service = ServiceTestSupport.CreateImageUploadService(db, media.Storage);
        var cases = new[]
        {
            (Origin: SKEncodedOrigin.TopRight, Width: 40, Height: 20),
            (Origin: SKEncodedOrigin.BottomRight, Width: 40, Height: 20),
            (Origin: SKEncodedOrigin.BottomLeft, Width: 40, Height: 20),
            (Origin: SKEncodedOrigin.LeftTop, Width: 20, Height: 40),
            (Origin: SKEncodedOrigin.RightTop, Width: 20, Height: 40),
            (Origin: SKEncodedOrigin.RightBottom, Width: 20, Height: 40),
            (Origin: SKEncodedOrigin.LeftBottom, Width: 20, Height: 40)
        };
        foreach (var testCase in cases)
        {
            var jpeg = TestImages.JpegWithOrientation(40, 20, testCase.Origin);
            using var stream = new MemoryStream(jpeg);
            var result = await service.UploadAsync(
                member.Id,
                stream,
                jpeg.Length,
                ServiceTestSupport.NewAudit(member.Id));

            Assert.Equal(testCase.Width, result.Width);
            Assert.Equal(testCase.Height, result.Height);
            var encoded = await File.ReadAllBytesAsync(media.ResolvePath(result.Url));
            var ascii = System.Text.Encoding.ASCII.GetString(encoded);
            Assert.DoesNotContain("Exif", ascii, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GPS", ascii, StringComparison.OrdinalIgnoreCase);

            using var data = SKData.CreateCopy(encoded);
            using var codec = SKCodec.Create(data);
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
            Assert.Equal(testCase.Width, codec.Info.Width);
            Assert.Equal(testCase.Height, codec.Info.Height);
        }
    }

    [DatabaseFact]
    public async Task Upload_accepts_gif_and_webp_and_always_emits_webp()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        using var media = new TestMediaStorage();

        var member = ServiceTestSupport.NewUser("upload-formats");
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var service = ServiceTestSupport.CreateImageUploadService(db, media.Storage);
        foreach (var bytes in new[] { TestImages.TinyGif(), TestImages.Webp(9, 7) })
        {
            using var stream = new MemoryStream(bytes);
            var result = await service.UploadAsync(
                member.Id,
                stream,
                bytes.Length,
                ServiceTestSupport.NewAudit(member.Id));
            Assert.Equal("image/webp", result.MimeType);
            Assert.EndsWith(".webp", result.Url, StringComparison.Ordinal);
            Assert.True(File.Exists(media.ResolvePath(result.Url)));
        }
    }

    [DatabaseFact]
    public async Task Upload_rejects_svg_executables_invalid_images_and_oversized_files_without_side_effects()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        using var media = new TestMediaStorage();

        var member = ServiceTestSupport.NewUser("upload-reject");
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var service = ServiceTestSupport.CreateImageUploadService(db, media.Storage);
        foreach (var bytes in new[] { TestImages.Svg(), TestImages.Executable(), [0xFF, 0xD8, 0xFF] })
        {
            using var stream = new MemoryStream(bytes);
            var error = await Assert.ThrowsAsync<ApiProblemException>(
                () => service.UploadAsync(
                    member.Id,
                    stream,
                    bytes.Length,
                    ServiceTestSupport.NewAudit(member.Id)));
            Assert.Equal(ApiErrorCodes.UploadUnsupportedType, error.Code);
            Assert.Equal(415, error.StatusCode);
        }

        var oversized = new byte[MediaLimits.MaxImageBytes + 1];
        using (var stream = new MemoryStream(oversized))
        {
            var error = await Assert.ThrowsAsync<ApiProblemException>(
                () => service.UploadAsync(
                    member.Id,
                    stream,
                    oversized.Length,
                    ServiceTestSupport.NewAudit(member.Id)));
            Assert.Equal(ApiErrorCodes.UploadTooLarge, error.Code);
            Assert.Equal(413, error.StatusCode);
        }

        Assert.Equal(0, await db.MediaUploads.CountAsync(upload => upload.OwnerId == member.Id));
        Assert.Empty(Directory.EnumerateFiles(media.RootPath, "*", SearchOption.AllDirectories));
    }

    [DatabaseFact]
    public async Task Upload_rejects_account_states_and_excessive_dimensions()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        using var media = new TestMediaStorage();

        var pending = ServiceTestSupport.NewUser("upload-pending", activated: false);
        var muted = ServiceTestSupport.NewUser(
            "upload-muted",
            status: UserStatuses.Muted,
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
            statusReason: "测试禁言");
        var banned = ServiceTestSupport.NewUser(
            "upload-banned",
            status: UserStatuses.Banned,
            statusReason: "测试封禁");
        var deleted = ServiceTestSupport.NewUser("upload-deleted");
        deleted.Status = UserStatuses.Deleted;
        deleted.DeletedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var member = ServiceTestSupport.NewUser("upload-active");
        var admin = ServiceTestSupport.NewUser("upload-admin", UserRoles.Admin);
        db.Users.AddRange(pending, muted, banned, deleted, member, admin);
        await db.SaveChangesAsync();

        var service = ServiceTestSupport.CreateImageUploadService(db, media.Storage);
        var png = TestImages.Png(5, 5);
        foreach (var userId in new[] { pending.Id, muted.Id, banned.Id, deleted.Id })
        {
            using var stream = new MemoryStream(png);
            var error = await Assert.ThrowsAsync<ApiProblemException>(
                () => service.UploadAsync(
                    userId,
                    stream,
                    png.Length,
                    ServiceTestSupport.NewAudit(userId)));
            Assert.Contains(
                error.Code,
                new[] { ApiErrorCodes.Forbidden, ApiErrorCodes.AccountMuted, ApiErrorCodes.AccountBanned });
        }

        using (var stream = new MemoryStream(png))
        {
            await service.UploadAsync(
                member.Id,
                stream,
                png.Length,
                ServiceTestSupport.NewAudit(member.Id));
        }

        using (var stream = new MemoryStream(png))
        {
            await service.UploadAsync(
                admin.Id,
                stream,
                png.Length,
                ServiceTestSupport.NewAudit(admin.Id));
        }

        var wide = TestImages.Png(MediaLimits.MaxImageDimension + 1, 1);
        using (var stream = new MemoryStream(wide))
        {
            var error = await Assert.ThrowsAsync<ApiProblemException>(
                () => service.UploadAsync(
                    member.Id,
                    stream,
                    wide.Length,
                    ServiceTestSupport.NewAudit(member.Id)));
            Assert.Equal(ApiErrorCodes.ValidationFailed, error.Code);
            Assert.Equal(400, error.StatusCode);
        }

        Assert.Equal(2, await db.MediaUploads.CountAsync(upload =>
            upload.OwnerId == member.Id || upload.OwnerId == admin.Id));
        Assert.Equal(
            2,
            Directory.EnumerateFiles(media.RootPath, "*", SearchOption.AllDirectories).Count());
    }
}