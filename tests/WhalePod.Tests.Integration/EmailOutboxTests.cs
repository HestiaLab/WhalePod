using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Communications;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class EmailOutboxTests
{
    [DatabaseFact]
    public async Task Notification_events_enqueue_email_only_when_enabled_and_never_for_self()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("email-post-author");
        var recipient = ServiceTestSupport.NewUser("email-recipient");
        recipient.Email = $"recipient-{ServiceTestSupport.NewSuffix()}@example.test";
        var replier = ServiceTestSupport.NewUser("email-replier");
        replier.Email = $"replier-{ServiceTestSupport.NewSuffix()}@example.test";
        var admin = ServiceTestSupport.NewUser("email-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var parent = ServiceTestSupport.NewComment(post.Id, recipient.Id, body: "接收回复的父评论");
        var ownParent = ServiceTestSupport.NewComment(post.Id, replier.Id, body: "回复者自己的评论");
        db.Users.AddRange(author, recipient, replier, admin);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.AddRange(parent, ownParent);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        await services.Comments.CreateAsync(
            replier.Id,
            post.Id,
            new CommentCreateCommand("第一条回复", parent.Id),
            ServiceTestSupport.NewAudit(replier.Id));

        var outbox = await db.EmailOutbox
            .AsNoTracking()
            .SingleAsync(email => email.UserId == recipient.Id);
        Assert.Equal(recipient.Email, outbox.ToAddress);
        Assert.Equal(NotificationTypes.Reply, outbox.Template);
        Assert.Equal(EmailStatuses.Pending, outbox.Status);
        var payload = outbox.PayloadJson.RootElement;
        Assert.Equal("有人回复了你的评论。", payload.GetProperty("summary").GetString());
        Assert.StartsWith("/posts/", payload.GetProperty("targetUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(EmailPreferenceTypes.RepliesMentions, payload.GetProperty("preferenceType").GetString());

        db.EmailPreferences.Add(new EmailPreference
        {
            UserId = recipient.Id,
            Type = EmailPreferenceTypes.RepliesMentions,
            Enabled = false,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await services.Comments.CreateAsync(
            replier.Id,
            post.Id,
            new CommentCreateCommand("第二条回复但已关闭邮件", parent.Id),
            ServiceTestSupport.NewAudit(replier.Id));
        Assert.Equal(1, await db.EmailOutbox.CountAsync(email => email.UserId == recipient.Id));
        Assert.Equal(2, await db.Notifications.CountAsync(notification =>
            notification.UserId == recipient.Id && notification.Type == NotificationTypes.Reply));

        await services.Comments.CreateAsync(
            replier.Id,
            post.Id,
            new CommentCreateCommand("回复自己不应产生邮件", ownParent.Id),
            ServiceTestSupport.NewAudit(replier.Id));
        Assert.Equal(0, await db.EmailOutbox.CountAsync(email => email.UserId == replier.Id));

        await services.AdminUsers.ChangeStatusAsync(
            admin.Id,
            recipient.Id,
            UserStatuses.Muted,
            "测试禁言",
            DateTimeOffset.UtcNow.AddDays(1),
            ServiceTestSupport.NewAudit(admin.Id));
        var securityEmail = await db.EmailOutbox
            .AsNoTracking()
            .SingleAsync(email => email.UserId == recipient.Id
                && email.Template == NotificationTypes.AccountStatusChanged);
        Assert.Equal(EmailStatuses.Pending, securityEmail.Status);
        Assert.Equal(
            EmailPreferenceTypes.AccountSecurity,
            securityEmail.PayloadJson.RootElement.GetProperty("preferenceType").GetString());
    }

    [DatabaseFact]
    public async Task Dispatch_marks_sent_and_renders_links_and_suppresses_security_unsubscribe()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var user = ServiceTestSupport.NewUser("email-dispatch");
        user.Email = $"dispatch-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var reply = AddOutbox(
            db,
            user,
            NotificationTypes.Reply,
            "有人回复了你的评论。",
            "/posts/00000000-0000-4000-8000-000000000001");
        var security = AddOutbox(
            db,
            user,
            NotificationTypes.AccountStatusChanged,
            "你的账号状态已更新。",
            null);
        await db.SaveChangesAsync();

        await EmailTestSupport.MarkOtherOutboxSentAsync(db, reply.Id, security.Id);
        var sender = new RecordingEmailSender();
        var dispatcher = EmailTestSupport.CreateDispatcher(db, sender);
        var result = await dispatcher.ProcessOnceAsync();

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, sender.Messages.Count);
        db.ChangeTracker.Clear();
        var storedReply = await db.EmailOutbox.SingleAsync(email => email.Id == reply.Id);
        Assert.Equal(EmailStatuses.Sent, storedReply.Status);
        Assert.NotNull(storedReply.SentAt);
        Assert.Equal(1, storedReply.Attempts);
        Assert.Null(storedReply.LockedUntil);
        var replyMessage = Assert.Single(sender.Messages, message =>
            message.Subject.Contains("回复", StringComparison.Ordinal));
        Assert.Contains("https://whalepod.test/posts/", replyMessage.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://whalepod.test/settings/emails", replyMessage.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://whalepod.test/email/unsubscribe?token=", replyMessage.TextBody, StringComparison.Ordinal);

        var securityMessage = Assert.Single(sender.Messages, message =>
            message.Subject.Contains("账号状态", StringComparison.Ordinal));
        Assert.DoesNotContain("/email/unsubscribe", securityMessage.TextBody, StringComparison.Ordinal);
        Assert.Contains("无法退订", securityMessage.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://whalepod.test/settings/emails", securityMessage.TextBody, StringComparison.Ordinal);
        var storedSecurity = await db.EmailOutbox.SingleAsync(email => email.Id == security.Id);
        Assert.Equal(EmailStatuses.Sent, storedSecurity.Status);
    }

    [DatabaseFact]
    public async Task Dispatch_retries_with_backoff_and_marks_failed_after_max_attempts()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var user = ServiceTestSupport.NewUser("email-retry");
        user.Email = $"retry-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var outbox = AddOutbox(
            db,
            user,
            NotificationTypes.Reply,
            "有人回复了你的评论。",
            "/posts/00000000-0000-4000-8000-000000000001");
        await db.SaveChangesAsync();

        await EmailTestSupport.MarkOtherOutboxSentAsync(db, outbox.Id);
        var sender = new RecordingEmailSender();
        sender.Results.Enqueue(new EmailSendResult(EmailSendOutcome.TransientFailure, "smtp_421"));
        sender.Results.Enqueue(new EmailSendResult(EmailSendOutcome.TransientFailure, "smtp_421"));
        var dispatcher = EmailTestSupport.CreateDispatcher(
            db,
            sender,
            EmailTestSupport.NewOptions(maxAttempts: 2, backoffBaseSeconds: 60));

        var first = await dispatcher.ProcessOnceAsync();
        Assert.Equal(1, first.Retried);
        db.ChangeTracker.Clear();
        var afterFirst = await db.EmailOutbox.SingleAsync(email => email.Id == outbox.Id);
        Assert.Equal(EmailStatuses.Pending, afterFirst.Status);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal("smtp_421", afterFirst.LastErrorCode);
        Assert.True(afterFirst.NextAttemptAt > DateTimeOffset.UtcNow);

        afterFirst.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var second = await dispatcher.ProcessOnceAsync();
        Assert.Equal(1, second.Failed);
        db.ChangeTracker.Clear();
        var afterSecond = await db.EmailOutbox.SingleAsync(email => email.Id == outbox.Id);
        Assert.Equal(EmailStatuses.Failed, afterSecond.Status);
        Assert.Equal(2, afterSecond.Attempts);
        Assert.Null(afterSecond.LockedUntil);
        Assert.Equal(2, sender.Messages.Count);
    }

    [DatabaseFact]
    public async Task Dispatch_suppresses_disabled_preferences_suppression_table_and_hard_bounces()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var disabled = ServiceTestSupport.NewUser("email-pref-disabled");
        disabled.Email = $"disabled-{ServiceTestSupport.NewSuffix()}@example.test";
        var suppressed = ServiceTestSupport.NewUser("email-suppressed");
        suppressed.Email = $"suppressed-{ServiceTestSupport.NewSuffix()}@example.test";
        var bounced = ServiceTestSupport.NewUser("email-bounced");
        bounced.Email = $"bounced-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.AddRange(disabled, suppressed, bounced);
        await db.SaveChangesAsync();
        db.EmailPreferences.Add(new EmailPreference
        {
            UserId = disabled.Id,
            Type = EmailPreferenceTypes.RepliesMentions,
            Enabled = false,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var options = EmailTestSupport.NewOptions();
        db.EmailSuppressions.Add(new EmailSuppression
        {
            EmailHash = new WhalePod.Infrastructure.Email.EmailAddressHasher(
                Microsoft.Extensions.Options.Options.Create(options)).Hash(suppressed.Email!),
            Reason = EmailSuppressionReasons.Manual,
            Source = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        var disabledMail = AddOutbox(db, disabled, NotificationTypes.Reply, "回复", null);
        var suppressedMail = AddOutbox(db, suppressed, NotificationTypes.Reply, "回复", null);
        var bouncedMail = AddOutbox(db, bounced, NotificationTypes.Reply, "回复", null);
        await db.SaveChangesAsync();

        await EmailTestSupport.MarkOtherOutboxSentAsync(
            db,
            disabledMail.Id,
            suppressedMail.Id,
            bouncedMail.Id);
        var sender = new RecordingEmailSender();
        sender.Results.Enqueue(new EmailSendResult(EmailSendOutcome.HardBounce, "smtp_550"));
        var dispatcher = EmailTestSupport.CreateDispatcher(db, sender, options);
        var result = await dispatcher.ProcessOnceAsync();

        Assert.Equal(0, result.Sent);
        Assert.Equal(3, result.Suppressed);
        Assert.Single(sender.Messages);
        db.ChangeTracker.Clear();
        Assert.Equal(
            EmailStatuses.Suppressed,
            (await db.EmailOutbox.SingleAsync(email => email.Id == disabledMail.Id)).Status);
        Assert.Equal(
            "preference_disabled",
            (await db.EmailOutbox.SingleAsync(email => email.Id == disabledMail.Id)).LastErrorCode);
        Assert.Equal(
            "suppressed",
            (await db.EmailOutbox.SingleAsync(email => email.Id == suppressedMail.Id)).LastErrorCode);
        Assert.Equal(
            "smtp_550",
            (await db.EmailOutbox.SingleAsync(email => email.Id == bouncedMail.Id)).LastErrorCode);
        var storedBounce = await db.EmailOutbox.SingleAsync(email => email.Id == bouncedMail.Id);
        Assert.Equal(EmailStatuses.Suppressed, storedBounce.Status);
        Assert.True(await db.EmailSuppressions.AnyAsync(candidate =>
            candidate.Reason == EmailSuppressionReasons.HardBounce));
    }

    [DatabaseFact]
    public async Task Dispatch_recovers_expired_sending_lease()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var user = ServiceTestSupport.NewUser("email-lease");
        user.Email = $"lease-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var outbox = AddOutbox(db, user, NotificationTypes.Reply, "有人回复了你的评论。", null);
        outbox.Status = EmailStatuses.Sending;
        outbox.Attempts = 1;
        outbox.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-5);
        outbox.NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();

        await EmailTestSupport.MarkOtherOutboxSentAsync(db, outbox.Id);
        var sender = new RecordingEmailSender();
        var dispatcher = EmailTestSupport.CreateDispatcher(db, sender);
        var result = await dispatcher.ProcessOnceAsync();

        Assert.Equal(1, result.Sent);
        db.ChangeTracker.Clear();
        var stored = await db.EmailOutbox.SingleAsync(email => email.Id == outbox.Id);
        Assert.Equal(EmailStatuses.Sent, stored.Status);
        Assert.Equal(2, stored.Attempts);
    }

    [DatabaseFact]
    public async Task Unsubscribe_token_is_signed_idempotent_and_rejects_security_category()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var user = ServiceTestSupport.NewUser("email-unsubscribe");
        user.Email = $"unsub-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var options = EmailTestSupport.NewOptions();
        var tokens = EmailTestSupport.CreateTokens(options);
        var service = EmailTestSupport.CreateUnsubscribeService(db, options);
        var token = tokens.Create(user.Id, EmailPreferenceTypes.ModerationResults);

        var first = await service.UnsubscribeAsync(token, ServiceTestSupport.NewAudit(user.Id));
        Assert.Equal(EmailUnsubscribeOutcome.Unsubscribed, first);
        var second = await service.UnsubscribeAsync(token, ServiceTestSupport.NewAudit(user.Id));
        Assert.Equal(EmailUnsubscribeOutcome.AlreadyDisabled, second);
        var preference = await db.EmailPreferences.SingleAsync(candidate =>
            candidate.UserId == user.Id && candidate.Type == EmailPreferenceTypes.ModerationResults);
        Assert.False(preference.Enabled);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "email.unsubscribed" && log.TargetId == user.Id));
        Assert.Equal(
            EmailUnsubscribeOutcome.InvalidToken,
            await service.UnsubscribeAsync("not-a-token", ServiceTestSupport.NewAudit(user.Id)));
        Assert.Throws<ArgumentException>(() =>
            tokens.Create(user.Id, EmailPreferenceTypes.AccountSecurity));
    }

    [DatabaseFact]
    public async Task Expired_lease_holder_cannot_overwrite_a_new_terminal_status()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var user = ServiceTestSupport.NewUser("email-fencing");
        user.Email = $"fencing-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.Add(user);
        var outbox = AddOutbox(
            db,
            user,
            NotificationTypes.Reply,
            "有人回复了你的评论。",
            "/posts/00000000-0000-4000-8000-000000000002");
        await db.SaveChangesAsync();
        await EmailTestSupport.MarkOtherOutboxSentAsync(db, outbox.Id);

        var blocking = new BlockingEmailSender();
        var options = EmailTestSupport.NewOptions();
        options.LeaseSeconds = 1;
        var firstDispatcher = EmailTestSupport.CreateDispatcher(db, blocking, options);
        var firstRun = firstDispatcher.ProcessOnceAsync();
        await blocking.Started.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var secondDb = ServiceTestSupport.CreateContext())
        {
            await secondDb.EmailOutbox
                .Where(email => email.Id == outbox.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(email => email.LockedUntil, DateTimeOffset.UtcNow.AddSeconds(-10)));
            var sender = new RecordingEmailSender();
            var secondDispatcher = EmailTestSupport.CreateDispatcher(secondDb, sender, options);
            var second = await secondDispatcher.ProcessOnceAsync();
            Assert.Equal(1, second.Sent);
        }

        blocking.Complete(new EmailSendResult(EmailSendOutcome.TransientFailure, "late_failure"));
        var first = await firstRun;
        Assert.Equal(0, first.Retried);

        db.ChangeTracker.Clear();
        var stored = await db.EmailOutbox.SingleAsync(email => email.Id == outbox.Id);
        Assert.Equal(EmailStatuses.Sent, stored.Status);
        Assert.Null(stored.LeaseToken);
        Assert.Null(stored.LockedUntil);
    }

    private sealed class BlockingEmailSender : IEmailSender
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<EmailSendResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<EmailSendResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            return await _result.Task.WaitAsync(cancellationToken);
        }

        public void Complete(EmailSendResult result)
        {
            _result.TrySetResult(result);
        }
    }

    private static EmailOutbox AddOutbox(
        WhalePodDbContext db,
        User user,
        string template,
        string summary,
        string? targetUrl)
    {
        var outbox = new EmailOutbox
        {
            ToAddress = user.Email!,
            UserId = user.Id,
            Template = template,
            PayloadJson = JsonSerializer.SerializeToDocument(new
            {
                summary,
                targetType = "comment",
                targetUrl,
                preferenceType = EmailPreferencePolicy.ForNotificationType(template)
            }),
            Status = EmailStatuses.Pending,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DedupeKey = $"test:{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.EmailOutbox.Add(outbox);
        return outbox;
    }
}