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
    public async Task<MediaCleanupResult> CleanupOnceAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var limit = Math.Clamp(batchSize, 1, 500);
        var candidateIds = await db.MediaUploads
            .AsNoTracking()
            .Where(upload => upload.Status == MediaUploadStatuses.Deleted
                || (upload.Status == MediaUploadStatuses.Temp && upload.ExpiresAt <= now))
            .OrderBy(upload => upload.ExpiresAt)
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
                    && (upload.Status != MediaUploadStatuses.Temp || upload.ExpiresAt > now)))
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
                logger.LogWarning(
                    exception,
                    "Failed to delete expired media object {MediaUploadId}; it will be retried.",
                    upload.Id);
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
}