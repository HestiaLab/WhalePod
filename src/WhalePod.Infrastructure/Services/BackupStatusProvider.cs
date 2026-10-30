using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WhalePod.Infrastructure.Services;

public sealed record BackupStatus(
    bool Configured,
    DateTimeOffset? LastSuccessAt,
    string? Database,
    long? Bytes);

public sealed class BackupStatusProvider(
    IConfiguration configuration,
    ILogger<BackupStatusProvider> logger)
{
    public BackupStatus Read()
    {
        var statusFilePath = configuration["Operations:Backup:StatusFilePath"];
        if (string.IsNullOrWhiteSpace(statusFilePath))
        {
            return new BackupStatus(false, null, null, null);
        }

        try
        {
            if (!File.Exists(statusFilePath))
            {
                return new BackupStatus(true, null, null, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(statusFilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning("Backup status file root is not a JSON object.");
                return new BackupStatus(true, null, null, null);
            }

            DateTimeOffset? lastSuccessAt = null;
            if (root.TryGetProperty("lastSuccessUtc", out var lastSuccess)
                && lastSuccess.ValueKind == JsonValueKind.String
                && lastSuccess.TryGetDateTimeOffset(out var parsedLastSuccess))
            {
                lastSuccessAt = parsedLastSuccess;
            }

            string? database = null;
            if (root.TryGetProperty("database", out var databaseElement)
                && databaseElement.ValueKind == JsonValueKind.String)
            {
                database = databaseElement.GetString();
            }

            long? bytes = null;
            if (root.TryGetProperty("bytes", out var bytesElement)
                && bytesElement.TryGetInt64(out var parsedBytes)
                && parsedBytes >= 0)
            {
                bytes = parsedBytes;
            }

            return new BackupStatus(true, lastSuccessAt, database, bytes);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            logger.LogWarning("Backup status file could not be read: {ErrorType}.", exception.GetType().Name);
            return new BackupStatus(true, null, null, null);
        }
    }
}