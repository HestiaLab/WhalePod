using Amazon.Runtime;
using Amazon.S3;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace WhalePod.Infrastructure.Storage;

public static class MediaStorageServiceCollectionExtensions
{
    public static IServiceCollection AddWhalePodMediaStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath,
        bool isDevelopment)
    {
        var section = configuration.GetSection("MediaStorage");
        var s3Section = section.GetSection("S3");
        var options = new MediaStorageOptions
        {
            Provider = section["Provider"] ?? string.Empty,
            PublicBaseUrl = section["PublicBaseUrl"] ?? "/media",
            LocalRootPath = section["LocalRootPath"],
            S3 = new S3MediaStorageOptions
            {
                ServiceUrl = s3Section["ServiceUrl"],
                BucketName = s3Section["BucketName"],
                Region = s3Section["Region"],
                AccessKeyId = s3Section["AccessKeyId"],
                SecretAccessKey = s3Section["SecretAccessKey"],
                ForcePathStyle = !bool.TryParse(s3Section["ForcePathStyle"], out var forcePathStyle)
                    || forcePathStyle
            }
        };
        var provider = (options.Provider ?? string.Empty).Trim();
        if (!string.Equals(provider, MediaStorageProviders.Local, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(provider, MediaStorageProviders.S3, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MediaStorage:Provider must be Local or S3.");
        }

        if (string.Equals(provider, MediaStorageProviders.Local, StringComparison.OrdinalIgnoreCase))
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException(
                    "Local media storage is only allowed in Development. Configure MediaStorage:Provider=S3 for non-development environments.");
            }

            if (string.IsNullOrWhiteSpace(options.LocalRootPath))
            {
                options.LocalRootPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "whalepod",
                    "media");
            }
            else if (!Path.IsPathRooted(options.LocalRootPath))
            {
                options.LocalRootPath = Path.GetFullPath(Path.Combine(contentRootPath, options.LocalRootPath));
            }

            options.PublicBaseUrl = NormalizeLocalPublicBaseUrl(options.PublicBaseUrl);
            Directory.CreateDirectory(options.LocalRootPath);
            services.AddSingleton<IOptions<MediaStorageOptions>>(Options.Create(options));
            services.AddSingleton<IMediaStorage, LocalMediaStorage>();
            return services;
        }

        ValidateS3Options(options);
        services.AddSingleton<IOptions<MediaStorageOptions>>(Options.Create(options));
        services.AddSingleton<IAmazonS3>(_ => CreateS3Client(options.S3));
        services.AddSingleton<IMediaStorage, S3MediaStorage>();
        return services;
    }

    private static string NormalizeLocalPublicBaseUrl(string publicBaseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(publicBaseUrl)
            ? "/media"
            : publicBaseUrl.Trim();
        if (!normalized.StartsWith('/')
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.Contains('\\')
            || normalized.Contains('?')
            || normalized.Contains('#'))
        {
            throw new InvalidOperationException(
                "MediaStorage:PublicBaseUrl must be a site-relative path when Provider=Local.");
        }

        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("MediaStorage:PublicBaseUrl cannot be the site root.");
        }

        return normalized;
    }

    private static void ValidateS3Options(MediaStorageOptions options)
    {
        var s3 = options.S3;
        if (string.IsNullOrWhiteSpace(s3.ServiceUrl)
            || !Uri.TryCreate(s3.ServiceUrl, UriKind.Absolute, out var serviceUri)
            || serviceUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("MediaStorage:S3:ServiceUrl must be an absolute HTTP(S) URL.");
        }

        if (string.IsNullOrWhiteSpace(s3.BucketName))
        {
            throw new InvalidOperationException("MediaStorage:S3:BucketName is required.");
        }

        var publicBaseUrl = options.PublicBaseUrl?.Trim() ?? string.Empty;
        var isSiteRelative = publicBaseUrl.StartsWith('/')
            && !publicBaseUrl.StartsWith("//", StringComparison.Ordinal);
        var isAbsoluteHttp = Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicBaseUri)
            && publicBaseUri.Scheme is "http" or "https";
        if (!isSiteRelative && !isAbsoluteHttp)
        {
            throw new InvalidOperationException(
                "MediaStorage:PublicBaseUrl must be an absolute HTTP(S) URL or site-relative path when Provider=S3.");
        }

        var hasAccessKey = !string.IsNullOrWhiteSpace(s3.AccessKeyId);
        var hasSecret = !string.IsNullOrWhiteSpace(s3.SecretAccessKey);
        if (hasAccessKey != hasSecret)
        {
            throw new InvalidOperationException(
                "MediaStorage:S3:AccessKeyId and SecretAccessKey must be configured together.");
        }

        s3.Region = string.IsNullOrWhiteSpace(s3.Region) ? "ap-guangzhou" : s3.Region.Trim();
        options.PublicBaseUrl = publicBaseUrl.TrimEnd('/');
        if (options.PublicBaseUrl.Length == 0)
        {
            throw new InvalidOperationException("MediaStorage:PublicBaseUrl is invalid.");
        }
    }

    private static IAmazonS3 CreateS3Client(S3MediaStorageOptions s3)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = s3.ServiceUrl,
            ForcePathStyle = s3.ForcePathStyle,
            AuthenticationRegion = s3.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };

        return string.IsNullOrWhiteSpace(s3.AccessKeyId)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(s3.AccessKeyId, s3.SecretAccessKey), config);
    }
}