using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record RetentionCleanupResult(
    int NotificationsDeleted,
    int EmailsDeleted,
    int CopyDedupesDeleted);

public sealed class RetentionCleanupService(
    WhalePodDbContext db,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionCleanupService> logger)
{
    public async Task<RetentionCleanupResult> CleanupOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var batchSize = Math.Clamp(settings.BatchSize, 1, 5000);
        var now = timeProvider.GetUtcNow();
        var notificationCutoff = now.AddDays(-Math.Max(1, settings.NotificationRetentionDays));
        var emailCutoff = now.AddDays(-Math.Max(1, settings.EmailOutboxRetentionDays));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var notificationIds = await db.Notifications
            .AsNoTracking()
            .Where(notification => notification.CreatedAt < notificationCutoff)
            .OrderBy(notification => notification.CreatedAt)
            .ThenBy(notification => notification.Id)
            .Take(batchSize)
            .Select(notification => notification.Id)
            .ToListAsync(cancellationToken);
        var notificationsDeleted = notificationIds.Count == 0
            ? 0
            : await db.Notifications
                .Where(notification => notificationIds.Contains(notification.Id))
                .ExecuteDeleteAsync(cancellationToken);

        var emailIds = await db.EmailOutbox
            .AsNoTracking()
            .Where(email => email.CreatedAt < emailCutoff
                && (email.Status == EmailStatuses.Sent
                    || email.Status == EmailStatuses.Failed
                    || email.Status == EmailStatuses.Suppressed))
            .OrderBy(email => email.CreatedAt)
            .ThenBy(email => email.Id)
            .Take(batchSize)
            .Select(email => email.Id)
            .ToListAsync(cancellationToken);
        var emailsDeleted = emailIds.Count == 0
            ? 0
            : await db.EmailOutbox
                .Where(email => emailIds.Contains(email.Id))
                .ExecuteDeleteAsync(cancellationToken);

        var copyDedupesDeleted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM copy_dedupe WHERE key_hash IN (SELECT key_hash FROM copy_dedupe WHERE expires_at <= {now} ORDER BY expires_at LIMIT {batchSize})",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        if (notificationsDeleted + emailsDeleted + copyDedupesDeleted > 0)
        {
            logger.LogInformation(
                "Retention cleanup removed {Notifications} notifications, {Emails} emails and {CopyDedupes} copy dedupe keys.",
                notificationsDeleted,
                emailsDeleted,
                copyDedupesDeleted);
        }

        return new RetentionCleanupResult(notificationsDeleted, emailsDeleted, copyDedupesDeleted);
    }
}