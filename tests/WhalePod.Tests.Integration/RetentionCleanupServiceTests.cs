using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Content;
using WhalePod.Domain.Interactions;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class RetentionCleanupServiceTests
{
    [DatabaseFact]
    public async Task Cleanup_removes_expired_notifications_emails_and_copy_dedupes_only()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var suffix = ServiceTestSupport.NewSuffix();
        var now = DateTimeOffset.UtcNow;
        var user = ServiceTestSupport.NewUser("retention");
        var oldNotification = new Notification
        {
            UserId = user.Id,
            Type = NotificationTypes.Reply,
            CreatedAt = now.AddDays(-200)
        };
        var recentNotification = new Notification
        {
            UserId = user.Id,
            Type = NotificationTypes.Reply,
            CreatedAt = now.AddDays(-10)
        };
        var oldSent = NewEmail($"retention-old-sent-{suffix}@example.test", EmailStatuses.Sent, now.AddDays(-40));
        var oldFailed = NewEmail($"retention-old-failed-{suffix}@example.test", EmailStatuses.Failed, now.AddDays(-40));
        var oldSuppressed = NewEmail($"retention-old-suppressed-{suffix}@example.test", EmailStatuses.Suppressed, now.AddDays(-40));
        var oldPending = NewEmail($"retention-old-pending-{suffix}@example.test", EmailStatuses.Pending, now.AddDays(-40));
        var recentSent = NewEmail($"retention-recent-sent-{suffix}@example.test", EmailStatuses.Sent, now.AddDays(-5));
        var expiredDedupe = new CopyDedupe
        {
            KeyHash = SHA256.HashData(Encoding.UTF8.GetBytes($"expired-{suffix}")),
            ExpiresAt = now.AddMinutes(-1)
        };
        var activeDedupe = new CopyDedupe
        {
            KeyHash = SHA256.HashData(Encoding.UTF8.GetBytes($"active-{suffix}")),
            ExpiresAt = now.AddMinutes(10)
        };

        db.Users.Add(user);
        db.Notifications.AddRange(oldNotification, recentNotification);
        db.EmailOutbox.AddRange(oldSent, oldFailed, oldSuppressed, oldPending, recentSent);
        db.CopyDedupes.AddRange(expiredDedupe, activeDedupe);
        await db.SaveChangesAsync();

        var service = new RetentionCleanupService(
            db,
            Options.Create(new RetentionOptions
            {
                NotificationRetentionDays = 180,
                EmailOutboxRetentionDays = 30,
                BatchSize = 100
            }),
            new FixedTimeProvider(now),
            NullLogger<RetentionCleanupService>.Instance);

        var result = await service.CleanupOnceAsync();

        Assert.True(result.NotificationsDeleted >= 1);
        Assert.True(result.EmailsDeleted >= 3);
        Assert.True(result.CopyDedupesDeleted >= 1);

        db.ChangeTracker.Clear();
        Assert.False(await db.Notifications.AnyAsync(notification => notification.Id == oldNotification.Id));
        Assert.True(await db.Notifications.AnyAsync(notification => notification.Id == recentNotification.Id));
        Assert.False(await db.EmailOutbox.AnyAsync(email => email.Id == oldSent.Id));
        Assert.False(await db.EmailOutbox.AnyAsync(email => email.Id == oldFailed.Id));
        Assert.False(await db.EmailOutbox.AnyAsync(email => email.Id == oldSuppressed.Id));
        Assert.True(await db.EmailOutbox.AnyAsync(email => email.Id == oldPending.Id));
        Assert.True(await db.EmailOutbox.AnyAsync(email => email.Id == recentSent.Id));
        Assert.False(await db.CopyDedupes.AnyAsync(dedupe => dedupe.KeyHash == expiredDedupe.KeyHash));
        Assert.True(await db.CopyDedupes.AnyAsync(dedupe => dedupe.KeyHash == activeDedupe.KeyHash));
    }

    private static EmailOutbox NewEmail(string address, string status, DateTimeOffset createdAt)
    {
        return new EmailOutbox
        {
            ToAddress = address,
            Template = "test",
            Status = status,
            CreatedAt = createdAt,
            NextAttemptAt = createdAt
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}