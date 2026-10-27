using Microsoft.Extensions.Options;

namespace WhalePod.Infrastructure.Storage;

public sealed class LocalMediaStorage : IMediaStorage
{
    private readonly string _rootPath;
    private readonly string _publicBaseUrl;

    public LocalMediaStorage(IOptions<MediaStorageOptions> options)
    {
        var value = options.Value;
        _rootPath = Path.GetFullPath(value.LocalRootPath
            ?? throw new InvalidOperationException("MediaStorage:LocalRootPath is required."));
        _publicBaseUrl = value.PublicBaseUrl;
    }

    public string ProviderName => "local";

    public async Task<string> SaveAsync(
        ReadOnlyMemory<byte> content,
        string objectKey,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (!MediaStoragePaths.IsValidObjectKey(objectKey))
        {
            throw new InvalidOperationException("Invalid media object key.");
        }

        var path = ResolvePath(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        return MediaStoragePaths.BuildPublicUrl(_publicBaseUrl, objectKey);
    }

    public Task DeleteAsync(string publicUrl, CancellationToken cancellationToken = default)
    {
        if (!MediaStoragePaths.TryResolveObjectKey(publicUrl, _publicBaseUrl, out var objectKey))
        {
            throw new InvalidOperationException("Media URL is not owned by the configured local storage.");
        }

        TryDelete(ResolvePath(objectKey));
        return Task.CompletedTask;
    }

    private string ResolvePath(string objectKey)
    {
        var path = Path.GetFullPath(Path.Combine(
            _rootPath,
            objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Media path escapes the configured storage root.");
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (FileNotFoundException)
        {
        }
    }
}