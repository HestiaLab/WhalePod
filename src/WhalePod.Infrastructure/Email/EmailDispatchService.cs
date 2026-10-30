using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Communications;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Email;

public sealed record EmailDispatchResult(int Sent, int Retried, int Failed, int Suppressed);

public sealed class EmailDispatchService(
    WhalePodDbContext db,
    IEmailSender sender,
    EmailTemplateRenderer renderer,
    EmailAddressHasher addressHasher,
    IOptions<EmailOptions> options,
    TimeProvider timeProvider,
    ILogger<EmailDispatchService> logger)
{
    private readonly EmailOptions _options = options.Value;

    public async Task<EmailDispatchResult> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        await RecoverExpiredLeasesAsync(cancellationToken);
        var claimed = await ClaimDueAsync(cancellationToken);
        var sent = 0;
        var retried = 0;
        var failed = 0;
        var suppressed = 0;

        foreach (var email in claimed)
        {
            var now = timeProvider.GetUtcNow();
            if (email.LeaseToken is not Guid leaseToken)
            {
                continue;
            }

            if (email.Attempts > Math.Max(1, _options.MaxAttempts))
            {
                if (await SettleAsync(
                        email,
                        leaseToken,
                        EmailStatuses.Failed,
                        null,
                        null,
                        "max_attempts",
                        cancellationToken))
                {
                    failed++;
                }

                continue;
            }

            var suppression = await db.EmailSuppressions
                .AsNoTracking()
                .AnyAsync(candidate => candidate.EmailHash == addressHasher.Hash(email.ToAddress), cancellationToken);
            if (suppression)
            {
                if (await SettleAsync(
                        email,
                        leaseToken,
                        EmailStatuses.Suppressed,
                        null,
                        null,
                        "suppressed",
                        cancellationToken))
                {
                    suppressed++;
                }

                continue;
            }

            var preferenceType = EmailPreferencePolicy.ForNotificationType(email.Template);
            if (preferenceType is null)
            {
                if (await SettleAsync(
                        email,
                        leaseToken,
                        EmailStatuses.Failed,
                        null,
                        null,
                        "template_invalid",
                        cancellationToken))
                {
                    failed++;
                }

                continue;
            }

            if (!EmailPreferencePolicy.IsSecurity(email.Template))
            {
                if (email.UserId is not Guid userId)
                {
                    if (await SettleAsync(
                            email,
                            leaseToken,
                            EmailStatuses.Suppressed,
                            null,
                            null,
                            "recipient_missing",
                            cancellationToken))
                    {
                        suppressed++;
                    }

                    continue;
                }

                var user = await db.Users
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == userId)
                    .Select(candidate => new { candidate.DeletedAt })
                    .SingleOrDefaultAsync(cancellationToken);
                if (user is null || user.DeletedAt is not null)
                {
                    if (await SettleAsync(
                            email,
                            leaseToken,
                            EmailStatuses.Suppressed,
                            null,
                            null,
                            "recipient_inactive",
                            cancellationToken))
                    {
                        suppressed++;
                    }

                    continue;
                }

                var enabled = await db.EmailPreferences
                    .AsNoTracking()
                    .Where(preference => preference.UserId == userId && preference.Type == preferenceType)
                    .Select(preference => (bool?)preference.Enabled)
                    .SingleOrDefaultAsync(cancellationToken);
                if (enabled == false)
                {
                    if (await SettleAsync(
                            email,
                            leaseToken,
                            EmailStatuses.Suppressed,
                            null,
                            null,
                            "preference_disabled",
                            cancellationToken))
                    {
                        suppressed++;
                    }

                    continue;
                }
            }

            EmailTemplateContent content;
            try
            {
                content = renderer.Render(email);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or JsonException
                or KeyNotFoundException)
            {
                logger.LogWarning(
                    "Email {EmailOutboxId} has an invalid template payload.",
                    email.Id);
                if (await SettleAsync(
                        email,
                        leaseToken,
                        EmailStatuses.Failed,
                        null,
                        null,
                        "template_invalid",
                        cancellationToken))
                {
                    failed++;
                }

                continue;
            }

            EmailSendResult result;
            try
            {
                result = await sender.SendAsync(
                    new EmailMessage(email.ToAddress, content.Subject, content.TextBody, content.HtmlBody),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Email {EmailOutboxId} transport threw {ErrorType}.",
                    email.Id,
                    exception.GetType().Name);
                result = new EmailSendResult(EmailSendOutcome.TransientFailure, "sender_exception");
            }

            switch (result.Outcome)
            {
                case EmailSendOutcome.Sent:
                    if (await SettleAsync(
                            email,
                            leaseToken,
                            EmailStatuses.Sent,
                            now,
                            null,
                            null,
                            cancellationToken))
                    {
                        sent++;
                    }

                    break;
                case EmailSendOutcome.HardBounce:
                    await SuppressAsync(email.ToAddress, EmailSuppressionReasons.HardBounce, now, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    if (await SettleAsync(
                            email,
                            leaseToken,
                            EmailStatuses.Suppressed,
                            null,
                            null,
                            result.ErrorCode ?? "hard_bounce",
                            cancellationToken))
                    {
                        suppressed++;
                    }

                    break;
                default:
                    if (email.Attempts >= Math.Max(1, _options.MaxAttempts))
                    {
                        if (await SettleAsync(
                                email,
                                leaseToken,
                                EmailStatuses.Failed,
                                null,
                                null,
                                result.ErrorCode ?? "transient",
                                cancellationToken))
                        {
                            failed++;
                        }
                    }
                    else if (await SettleAsync(
                                 email,
                                 leaseToken,
                                 EmailStatuses.Pending,
                                 null,
                                 now.Add(CalculateBackoff(email.Attempts)),
                                 result.ErrorCode ?? "transient",
                                 cancellationToken))
                    {
                        retried++;
                    }

                    break;
            }
        }

        logger.LogInformation(
            "Email dispatch finished: sent {Sent}, retried {Retried}, failed {Failed}, suppressed {Suppressed}.",
            sent,
            retried,
            failed,
            suppressed);
        return new EmailDispatchResult(sent, retried, failed, suppressed);
    }

    private async Task RecoverExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.EmailOutbox
            .Where(email => email.Status == EmailStatuses.Sending
                && (email.LockedUntil == null || email.LockedUntil <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(email => email.Status, EmailStatuses.Pending)
                    .SetProperty(email => email.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(email => email.LeaseToken, (Guid?)null)
                    .SetProperty(email => email.NextAttemptAt, now),
                cancellationToken);
    }

    private async Task<List<EmailOutbox>> ClaimDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var limit = Math.Clamp(_options.BatchSize, 1, 200);
        var lease = TimeSpan.FromSeconds(Math.Max(30, _options.LeaseSeconds));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.EmailOutbox
            .FromSqlInterpolated($"""
                SELECT * FROM email_outbox
                WHERE status = 'pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at, id
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        foreach (var email in rows)
        {
            email.Status = EmailStatuses.Sending;
            email.Attempts++;
            email.LockedUntil = now.Add(lease);
            email.LeaseToken = Guid.NewGuid();
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    private async Task SuppressAsync(
        string toAddress,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var hash = addressHasher.Hash(toAddress);
        var existing = await db.EmailSuppressions
            .SingleOrDefaultAsync(candidate => candidate.EmailHash == hash, cancellationToken);
        if (existing is null)
        {
            db.EmailSuppressions.Add(new EmailSuppression
            {
                EmailHash = hash,
                Reason = reason,
                Source = "smtp",
                CreatedAt = now,
                LastSeenAt = now
            });
        }
        else
        {
            existing.LastSeenAt = now;
        }
    }

    private async Task<bool> SettleAsync(
        EmailOutbox email,
        Guid leaseToken,
        string status,
        DateTimeOffset? sentAt,
        DateTimeOffset? nextAttemptAt,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var updated = await db.EmailOutbox
            .Where(row => row.Id == email.Id
                && row.Status == EmailStatuses.Sending
                && row.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, status)
                    .SetProperty(row => row.SentAt, sentAt)
                    .SetProperty(row => row.NextAttemptAt, row => nextAttemptAt ?? row.NextAttemptAt)
                    .SetProperty(row => row.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(row => row.LastErrorCode, errorCode)
                    .SetProperty(row => row.LeaseToken, (Guid?)null),
                cancellationToken);
        if (updated == 0)
        {
            logger.LogWarning(
                "Email {EmailOutboxId} lease was lost before settling; result discarded.",
                email.Id);
        }

        return updated > 0;
    }

    private TimeSpan CalculateBackoff(int attempts)
    {
        var baseSeconds = Math.Max(1, _options.BackoffBaseSeconds);
        var maxSeconds = Math.Max(baseSeconds, _options.BackoffMaxSeconds);
        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var seconds = Math.Min(maxSeconds, baseSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}