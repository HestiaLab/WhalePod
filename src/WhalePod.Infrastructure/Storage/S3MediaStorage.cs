using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Options;

namespace WhalePod.Infrastructure.Storage;

public sealed class S3MediaStorage(
    IAmazonS3 s3,
    IOptions<MediaStorageOptions> options) : IMediaStorage
{
    private readonly MediaStorageOptions _options = options.Value;

    public string ProviderName => "s3";

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

        using var stream = new MemoryStream(content.ToArray(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _options.S3.BucketName!,
            Key = objectKey,
            InputStream = stream,
            ContentType = contentType
        };
        request.Headers.CacheControl = "public, max-age=31536000, immutable";
        await s3.PutObjectAsync(request, cancellationToken);
        return MediaStoragePaths.BuildPublicUrl(_options.PublicBaseUrl, objectKey);
    }

    public async Task DeleteAsync(string publicUrl, CancellationToken cancellationToken = default)
    {
        if (!MediaStoragePaths.TryResolveObjectKey(publicUrl, _options.PublicBaseUrl, out var objectKey))
        {
            throw new InvalidOperationException("Media URL is not owned by the configured object storage.");
        }

        await s3.DeleteObjectAsync(_options.S3.BucketName!, objectKey, cancellationToken);
    }
}