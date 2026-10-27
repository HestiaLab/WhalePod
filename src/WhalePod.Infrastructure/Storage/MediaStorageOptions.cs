using System.Text.RegularExpressions;

namespace WhalePod.Infrastructure.Storage;

public static class MediaStorageProviders
{
    public const string Local = "Local";
    public const string S3 = "S3";
}

public sealed class MediaStorageOptions
{
    public string Provider { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = "/media";
    public string? LocalRootPath { get; set; }
    public S3MediaStorageOptions S3 { get; set; } = new();
}

public sealed class S3MediaStorageOptions
{
    public string? ServiceUrl { get; set; }
    public string? BucketName { get; set; }
    public string? Region { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public bool ForcePathStyle { get; set; } = true;
}

public interface IMediaStorage
{
    string ProviderName { get; }

    Task<string> SaveAsync(
        ReadOnlyMemory<byte> content,
        string objectKey,
        string contentType,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string publicUrl, CancellationToken cancellationToken = default);
}

internal static partial class MediaStoragePaths
{
    private static readonly Regex ObjectKeyPattern = CreateObjectKeyPattern();

    public static string BuildObjectKey(DateTimeOffset now, string extension)
    {
        return $"{now.UtcDateTime:yyyy}/{now.UtcDateTime:MM}/{Guid.NewGuid():N}.{extension}";
    }

    public static string BuildPublicUrl(string publicBaseUrl, string objectKey)
    {
        return $"{publicBaseUrl.TrimEnd('/')}/{objectKey}";
    }

    public static bool IsValidObjectKey(string objectKey)
    {
        return ObjectKeyPattern.IsMatch(objectKey);
    }

    public static bool TryResolveObjectKey(
        string publicUrl,
        string publicBaseUrl,
        out string objectKey)
    {
        objectKey = string.Empty;
        if (string.IsNullOrWhiteSpace(publicUrl) || string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return false;
        }

        string relativePath;
        if (Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri)
            && baseUri.Scheme is "http" or "https")
        {
            if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri)
                || publicUri.Scheme != baseUri.Scheme
                || publicUri.Host != baseUri.Host
                || publicUri.Port != baseUri.Port
                || !string.IsNullOrEmpty(publicUri.Query)
                || !string.IsNullOrEmpty(publicUri.Fragment))
            {
                return false;
            }

            var basePath = baseUri.AbsolutePath.TrimEnd('/');
            var path = publicUri.AbsolutePath;
            if (!path.StartsWith(basePath + "/", StringComparison.Ordinal))
            {
                return false;
            }

            relativePath = path[(basePath.Length + 1)..];
            if (Uri.UnescapeDataString(relativePath) != relativePath)
            {
                return false;
            }
        }
        else
        {
            if (!publicBaseUrl.StartsWith('/')
                || publicBaseUrl.StartsWith("//", StringComparison.Ordinal)
                || publicBaseUrl.Contains('?')
                || publicBaseUrl.Contains('#')
                || publicUrl.Contains('?')
                || publicUrl.Contains('#'))
            {
                return false;
            }

            var basePath = publicBaseUrl.TrimEnd('/');
            if (!publicUrl.StartsWith(basePath + "/", StringComparison.Ordinal))
            {
                return false;
            }

            relativePath = publicUrl[(basePath.Length + 1)..];
        }

        if (!IsValidObjectKey(relativePath))
        {
            return false;
        }

        objectKey = relativePath;
        return true;
    }

    [GeneratedRegex(@"^\d{4}/\d{2}/[0-9a-f]{32}\.(?:jpg|png|webp|gif)$", RegexOptions.CultureInvariant)]
    private static partial Regex CreateObjectKeyPattern();
}

public static class MediaLimits
{
    public const long MaxImageBytes = 10 * 1024 * 1024;
    public const int MaxImageDimension = 8192;
    public const long MaxImagePixels = 25_000_000;
    public const int MaxAnimationFrames = 64;
    public const long MaxAnimationPixels = 100_000_000;
}