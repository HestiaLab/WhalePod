using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Interactions;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Infrastructure.Email;

public sealed class EmailNotificationQueue(WhalePodDbContext db)
{
    public async Task<EmailOutbox?> EnqueueForNotificationAsync(
        Guid recipientUserId,
        string notificationType,
        object payload,
        string dedupeKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var preferenceType = EmailPreferencePolicy.ForNotificationType(notificationType);
        if (preferenceType is null)
        {
            return null;
        }

        var user = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == recipientUserId
                && candidate.Email != null
                && candidate.Email != ""
                && candidate.DeletedAt == null)
            .Select(candidate => new { candidate.Email })
            .SingleOrDefaultAsync(cancellationToken);
        if (user?.Email is null)
        {
            return null;
        }

        if (!EmailPreferencePolicy.IsSecurity(notificationType))
        {
            var enabled = await db.EmailPreferences
                .AsNoTracking()
                .Where(preference => preference.UserId == recipientUserId && preference.Type == preferenceType)
                .Select(preference => (bool?)preference.Enabled)
                .SingleOrDefaultAsync(cancellationToken);
            if (enabled == false)
            {
                return null;
            }
        }

        var payloadJson = JsonSerializer.SerializeToDocument(payload);
        var notification = new Notification
        {
            UserId = recipientUserId,
            Type = notificationType,
            PayloadJson = payloadJson
        };
        var presentation = NotificationPresentation.Map(notification);
        var edgeDedupeKey = BuildDedupeKey(recipientUserId, dedupeKey);
        var toAddress = user.Email.Trim();
        var exists = await db.EmailOutbox
            .AsNoTracking()
            .AnyAsync(email => email.ToAddress == toAddress && email.DedupeKey == edgeDedupeKey, cancellationToken);
        if (exists)
        {
            return null;
        }

        var outbox = new EmailOutbox
        {
            ToAddress = toAddress,
            UserId = recipientUserId,
            Template = notificationType,
            PayloadJson = JsonSerializer.SerializeToDocument(new
            {
                summary = presentation.Summary,
                targetType = presentation.TargetType,
                targetId = presentation.TargetId,
                targetUrl = presentation.TargetUrl,
                preferenceType,
                action = TryGetAction(payloadJson)
            }),
            Status = EmailStatuses.Pending,
            Attempts = 0,
            NextAttemptAt = now,
            DedupeKey = edgeDedupeKey,
            CreatedAt = now
        };
        db.EmailOutbox.Add(outbox);
        return outbox;
    }

    public EmailOutbox? EnqueueAccountDeleted(
        Guid userId,
        string? toAddress,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            return null;
        }

        var outbox = new EmailOutbox
        {
            ToAddress = toAddress.Trim(),
            UserId = userId,
            Template = "account_deleted",
            PayloadJson = JsonSerializer.SerializeToDocument(new
            {
                summary = "你的账号已完成注销。",
                targetType = "account",
                targetUrl = "/",
                preferenceType = EmailPreferenceTypes.AccountSecurity
            }),
            Status = EmailStatuses.Pending,
            Attempts = 0,
            NextAttemptAt = now,
            DedupeKey = $"account-deleted:{userId:N}",
            CreatedAt = now
        };
        db.EmailOutbox.Add(outbox);
        return outbox;
    }

    private static string BuildDedupeKey(Guid userId, string dedupeKey)
    {
        var key = $"notification:{userId:N}:{dedupeKey}";
        if (key.Length <= 200)
        {
            return key;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dedupeKey));
        return $"notification:{userId:N}:{Convert.ToHexString(hash)}";
    }

    private static string? TryGetAction(JsonDocument payload)
    {
        return payload.RootElement.ValueKind == JsonValueKind.Object
            && payload.RootElement.TryGetProperty("action", out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }
}