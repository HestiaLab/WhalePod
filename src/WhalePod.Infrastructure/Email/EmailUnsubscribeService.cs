using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Infrastructure.Email;

public enum EmailUnsubscribeOutcome
{
    Unsubscribed,
    AlreadyDisabled,
    InvalidToken,
    UserMissing
}

public sealed class EmailUnsubscribeService(
    WhalePodDbContext db,
    EmailUnsubscribeTokenService tokens,
    TimeProvider timeProvider)
{
    public async Task<EmailUnsubscribeOutcome> UnsubscribeAsync(
        string? token,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!tokens.TryValidate(token, out var userId, out var preferenceType, out _))
        {
            return EmailUnsubscribeOutcome.InvalidToken;
        }

        var userExists = await db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == userId && user.DeletedAt == null, cancellationToken);
        if (!userExists)
        {
            return EmailUnsubscribeOutcome.UserMissing;
        }

        var now = timeProvider.GetUtcNow();
        var preference = await db.EmailPreferences
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == userId && candidate.Type == preferenceType,
                cancellationToken);
        if (preference is { Enabled: false })
        {
            return EmailUnsubscribeOutcome.AlreadyDisabled;
        }

        if (preference is null)
        {
            db.EmailPreferences.Add(new EmailPreference
            {
                UserId = userId,
                Type = preferenceType,
                Enabled = false,
                UpdatedAt = now
            });
        }
        else
        {
            preference.Enabled = false;
            preference.UpdatedAt = now;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = userId,
            Action = "email.unsubscribed",
            TargetType = "user",
            TargetId = userId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                preferenceType,
                changed = true
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        return EmailUnsubscribeOutcome.Unsubscribed;
    }
}