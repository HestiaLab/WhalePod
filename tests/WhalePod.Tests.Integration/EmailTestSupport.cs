using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Tests.Integration;

internal static class EmailTestSupport
{
    public static EmailOptions NewOptions(
        int maxAttempts = 5,
        int backoffBaseSeconds = 60,
        int backoffMaxSeconds = 3600)
    {
        return new EmailOptions
        {
            Provider = EmailProviders.Logging,
            PublicBaseUrl = "https://whalepod.test",
            SiteName = "WhalePod 测试",
            SuppressionHashSecret = "test-only-email-suppression-secret-0123456789",
            UnsubscribeSecret = "test-only-email-unsubscribe-secret-0123456789",
            UnsubscribeTokenLifetimeDays = 30,
            BatchSize = 20,
            LeaseSeconds = 300,
            MaxAttempts = maxAttempts,
            BackoffBaseSeconds = backoffBaseSeconds,
            BackoffMaxSeconds = backoffMaxSeconds
        };
    }

    public static EmailUnsubscribeTokenService CreateTokens(EmailOptions options)
    {
        return new EmailUnsubscribeTokenService(Options.Create(options), TimeProvider.System);
    }

    public static EmailTemplateRenderer CreateRenderer(EmailOptions options)
    {
        return new EmailTemplateRenderer(
            Options.Create(options),
            CreateTokens(options));
    }

    public static EmailDispatchService CreateDispatcher(
        WhalePodDbContext db,
        IEmailSender sender,
        EmailOptions? options = null)
    {
        options ??= NewOptions();
        var wrapped = Options.Create(options);
        var hasher = new EmailAddressHasher(wrapped);
        return new EmailDispatchService(
            db,
            sender,
            new EmailTemplateRenderer(wrapped, new EmailUnsubscribeTokenService(wrapped, TimeProvider.System)),
            hasher,
            wrapped,
            TimeProvider.System,
            NullLogger<EmailDispatchService>.Instance);
    }

    public static EmailUnsubscribeService CreateUnsubscribeService(
        WhalePodDbContext db,
        EmailOptions? options = null)
    {
        options ??= NewOptions();
        var wrapped = Options.Create(options);
        return new EmailUnsubscribeService(
            db,
            new EmailUnsubscribeTokenService(wrapped, TimeProvider.System),
            TimeProvider.System);
    }

    public static Task MarkOtherOutboxSentAsync(
        WhalePodDbContext db,
        params Guid[] keepIds)
    {
        return db.EmailOutbox
            .Where(email => !keepIds.Contains(email.Id)
                && (email.Status == EmailStatuses.Pending || email.Status == EmailStatuses.Sending))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(email => email.Status, EmailStatuses.Sent)
                .SetProperty(email => email.LockedUntil, (DateTimeOffset?)null));
    }
}

internal sealed class RecordingEmailSender : IEmailSender
{
    public List<EmailMessage> Messages { get; } = [];

    public Queue<EmailSendResult> Results { get; } = new();

    public Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.FromResult(
            Results.Count == 0
                ? new EmailSendResult(EmailSendOutcome.Sent)
                : Results.Dequeue());
    }
}