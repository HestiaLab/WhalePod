using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using WhalePod.Domain;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Infrastructure.Services;

public sealed record MediaCleanupResult(int Deleted, int Failed);

public sealed class MediaCleanupService(
    WhalePodDbContext db,
    IMediaStorage storage,
    TimeProvider timeProvider,
    ILogger<MediaCleanupService> logger)
{
    private const int QuarantineAfterAttempts = 5;

    public async Task<MediaCleanupResult> CleanupOnceAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var limit = Math.Clamp(batchSize, 1, 500);
        var candidateIds = await db.MediaUploads
            .AsNoTracking()
            .Where(upload => (upload.Status == MediaUploadStatuses.Deleted
                    || (upload.Status == MediaUploadStatuses.Temp && upload.ExpiresAt <= now))
                && (upload.NextAttemptAt == null || upload.NextAttemptAt <= now))
            .OrderBy(upload => upload.NextAttemptAt ?? DateTimeOffset.MinValue)
            .ThenBy(upload => upload.ExpiresAt)
            .ThenBy(upload => upload.Id)
            .Take(limit)
            .Select(upload => upload.Id)
            .ToListAsync(cancellationToken);

        var deleted = 0;
        var failed = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var candidateId in candidateIds)
        {
            var upload = await db.MediaUploads
                .FromSqlInterpolated($"SELECT * FROM media_upload WHERE id = {candidateId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (upload is null
                || (upload.Status != MediaUploadStatuses.Deleted
                    && (upload.Status != MediaUploadStatuses.Temp || upload.ExpiresAt > now))
                || (upload.NextAttemptAt is not null && upload.NextAttemptAt > now))
            {
                continue;
            }

            try
            {
                await storage.DeleteAsync(upload.Url, cancellationToken);
            }
            catch (Exception exception)
            {
                failed++;
                upload.CleanupAttempts++;
                upload.NextAttemptAt = upload.CleanupAttempts >= QuarantineAfterAttempts
                    ? now.AddDays(7)
                    : now.Add(Backoff(upload.CleanupAttempts));
                if (upload.CleanupAttempts == QuarantineAfterAttempts)
                {
                    db.AuditLogs.Add(AuditLogWriter.CreateSystemAction(
                        "media.cleanup_quarantined",
                        "media_upload",
                        upload.Id,
                        new
                        {
                            attempts = upload.CleanupAttempts,
                            nextAttemptAt = upload.NextAttemptAt
                        },
                        now));
                    logger.LogError(
                        exception,
                        "Media object {MediaUploadId} failed {Attempts} cleanup attempts and was quarantined until {NextAttemptAt}.",
                        upload.Id,
                        upload.CleanupAttempts,
                        upload.NextAttemptAt);
                }
                else
                {
                    logger.LogWarning(
                        exception,
                        "Failed to delete expired media object {MediaUploadId}; retrying at {NextAttemptAt}.",
                        upload.Id,
                        upload.NextAttemptAt);
                }

                continue;
            }

            db.MediaUploads.Remove(upload);
            deleted++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Media cleanup removed {Deleted} uploads and failed {Failed} objects.",
            deleted,
            failed);
        return new MediaCleanupResult(deleted, failed);
    }

    private static TimeSpan Backoff(int attempts)
    {
        var minutes = Math.Min(360, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 10)));
        return TimeSpan.FromMinutes(minutes);
    }
}