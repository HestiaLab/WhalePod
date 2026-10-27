using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Domain.Interactions;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class GovernanceServiceTests
{
    [DatabaseFact]
    public async Task Moderation_queue_and_actions_follow_state_machine_and_notify_author()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("mod-author");
        var admin = ServiceTestSupport.NewUser("mod-admin", UserRoles.Admin);
        var reporter = ServiceTestSupport.NewUser("mod-reporter");
        var category = ServiceTestSupport.NewCategory();
        var pending = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Pending);
        var rejected = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Pending);
        var escalated = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Pending);
        var hiddenCandidate = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Pending);
        var pendingComment = ServiceTestSupport.NewComment(pending.Id, author.Id, ModerationStatuses.Pending);
        db.Users.AddRange(author, admin, reporter);
        db.Categories.Add(category);
        db.Posts.AddRange(pending, rejected, escalated, hiddenCandidate);
        db.Comments.Add(pendingComment);
        db.Reports.Add(new Report
        {
            ReporterId = reporter.Id,
            TargetType = ReportTargetTypes.Post,
            TargetId = pending.Id,
            Reason = ReportReasons.Spam,
            Status = ReportStatuses.Pending
        });
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var queuedPost = await ServiceTestSupport.FindQueueItemAsync(
            services.Moderation,
            admin.Id,
            "post",
            pending.Id);
        Assert.NotNull(queuedPost);
        Assert.Equal(1, queuedPost.ReportCount);
        Assert.Null(queuedPost.AiSuggestion);
        var queuedComment = await ServiceTestSupport.FindQueueItemAsync(
            services.Moderation,
            admin.Id,
            "comment",
            pendingComment.Id);
        Assert.NotNull(queuedComment);

        var approve = await services.Moderation.ExecuteAsync(
            admin.Id,
            "post",
            pending.Id,
            new ModerationActionCommand("approve", "内容可复现", 1, null, null),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(ModerationStatuses.Approved, approve.ModerationStatus);
        Assert.Equal(2, approve.Version);
        Assert.NotNull(await services.Content.GetPostAsync(pending.Id));
        Assert.True(await db.Notifications.AnyAsync(notification =>
            notification.UserId == author.Id && notification.Type == NotificationTypes.PostApproved));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "moderation.approve" && log.TargetId == pending.Id && log.IpHash != null));

        var missingReason = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Moderation.ExecuteAsync(
                admin.Id,
                "post",
                rejected.Id,
                new ModerationActionCommand("reject", null, 1, null, null),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, missingReason.Code);

        var reject = await services.Moderation.ExecuteAsync(
            admin.Id,
            "post",
            rejected.Id,
            new ModerationActionCommand("reject", "包含未披露推广", 1, null, null),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(ModerationStatuses.Rejected, reject.ModerationStatus);
        Assert.True(await db.Notifications.AnyAsync(notification =>
            notification.UserId == author.Id && notification.Type == NotificationTypes.PostRejected));
        var rejectedDetail = Assert.IsType<PostDetailView>(
            await services.Content.GetPostForViewerAsync(rejected.Id, author.Id, false));
        Assert.Equal("包含未披露推广", rejectedDetail.LatestModerationReason);

        var stale = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Moderation.ExecuteAsync(
                admin.Id,
                "post",
                hiddenCandidate.Id,
                new ModerationActionCommand("approve", null, 99, null, null),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ContentVersionStale, stale.Code);

        var illegal = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Moderation.ExecuteAsync(
                admin.Id,
                "post",
                hiddenCandidate.Id,
                new ModerationActionCommand("hide", "非法从 pending 隐藏", 1, null, null),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Conflict, illegal.Code);

        var escalate = await services.Moderation.ExecuteAsync(
            admin.Id,
            "post",
            escalated.Id,
            new ModerationActionCommand("escalate", "需要优先人工复核", 1, null, null),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(ModerationStatuses.Pending, escalate.ModerationStatus);
        Assert.Equal(2, escalate.Version);
        db.ChangeTracker.Clear();
        var escalatedPost = await db.Posts.SingleAsync(post => post.Id == escalated.Id);
        Assert.Equal(ModerationStatuses.Pending, escalatedPost.ModerationStatus);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "moderation.escalate" && log.TargetId == escalated.Id));
    }

    [DatabaseFact]
    public async Task Reports_are_created_and_resolved_with_content_and_user_actions_without_leaking_reporter()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("report-author");
        var reporter = ServiceTestSupport.NewUser("report-reporter");
        var admin = ServiceTestSupport.NewUser("report-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var secondPost = ServiceTestSupport.NewPost(author.Id, category.Id);
        db.Users.AddRange(author, reporter, admin);
        db.Categories.Add(category);
        db.Posts.AddRange(post, secondPost);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var receipt = await services.Reports.CreateAsync(
            reporter.Id,
            new ReportCreateCommand("post", post.Id, "spam", "包含推广链接"),
            ServiceTestSupport.NewAudit(reporter.Id));
        Assert.Equal(ReportStatuses.Pending, receipt.Status);
        Assert.Equal("post", receipt.TargetType);

        var duplicate = await services.Reports.CreateAsync(
            reporter.Id,
            new ReportCreateCommand("post", secondPost.Id, "harassment", null),
            ServiceTestSupport.NewAudit(reporter.Id));
        Assert.Equal(ReportStatuses.Pending, duplicate.Status);

        var listedReceipt = Assert.IsType<AdminReportView>(
            await FindReportAsync(services.Reports, admin.Id, "pending", receipt.Id));
        Assert.Equal(reporter.Handle, listedReceipt.Reporter.Handle);
        Assert.Contains("测试帖子", listedReceipt.TargetPreview, StringComparison.Ordinal);
        Assert.NotNull(await FindReportAsync(services.Reports, admin.Id, "pending", duplicate.Id));

        var resolved = await services.Reports.ResolveAsync(
            admin.Id,
            receipt.Id,
            new ReportResolveCommand(
                "resolved",
                "确认推广并处罚作者",
                [
                    new ReportResolveActionInput("content_hide", "与主题无关的推广内容", null),
                    new ReportResolveActionInput("user_mute", "多次发布推广内容", DateTimeOffset.UtcNow.AddDays(7))
                ]),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(ReportStatuses.Resolved, resolved.Status);
        Assert.NotNull(resolved.HandledAt);
        Assert.Equal("确认推广并处罚作者", resolved.ResolutionNote);

        db.ChangeTracker.Clear();
        var hiddenPost = await db.Posts.SingleAsync(candidate => candidate.Id == post.Id);
        Assert.Equal(ModerationStatuses.Hidden, hiddenPost.ModerationStatus);
        var mutedAuthor = await db.Users.SingleAsync(candidate => candidate.Id == author.Id);
        Assert.Equal(UserStatuses.Muted, mutedAuthor.Status);
        Assert.NotNull(mutedAuthor.StatusExpiresAt);
        Assert.Null(await services.Content.GetPostAsync(post.Id));

        var reporterNotification = await db.Notifications.SingleAsync(notification =>
            notification.UserId == reporter.Id && notification.Type == NotificationTypes.ReportResult);
        Assert.DoesNotContain(
            reporter.Handle,
            reporterNotification.PayloadJson.RootElement.GetRawText());
        Assert.True(await db.Notifications.AnyAsync(notification =>
            notification.UserId == author.Id && notification.Type == NotificationTypes.AccountStatusChanged));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "report.resolved" && log.TargetId == receipt.Id && log.IpHash != null));
        Assert.True(await db.ModerationActions.AnyAsync(action =>
            action.TargetId == post.Id && action.Action == ModerationActions.Hide));

        var repeat = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Reports.ResolveAsync(
                admin.Id,
                receipt.Id,
                new ReportResolveCommand("dismissed", "重复处理", null),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Conflict, repeat.Code);

        var dismissedWithActions = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Reports.ResolveAsync(
                admin.Id,
                duplicate.Id,
                new ReportResolveCommand(
                    "dismissed",
                    "驳回但带动作",
                    [new ReportResolveActionInput("content_delete", "不应执行", null)]),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, dismissedWithActions.Code);

        var dismissed = await services.Reports.ResolveAsync(
            admin.Id,
            duplicate.Id,
            new ReportResolveCommand("dismissed", "证据不足", null),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(ReportStatuses.Dismissed, dismissed.Status);
        Assert.True(await db.Notifications.AnyAsync(notification =>
            notification.UserId == author.Id
            && notification.DedupeKey == $"report-result:target:{duplicate.Id}"));
        Assert.NotNull(await FindReportAsync(services.Reports, admin.Id, "dismissed", duplicate.Id));
    }

    [DatabaseFact]
    public async Task Muted_member_can_report_but_banned_member_cannot()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("report-state-author");
        var muted = ServiceTestSupport.NewUser(
            "report-state-muted",
            status: UserStatuses.Muted,
            statusReason: "测试禁言",
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1));
        var banned = ServiceTestSupport.NewUser(
            "report-state-banned",
            status: UserStatuses.Banned,
            statusReason: "测试封禁");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        db.Users.AddRange(author, muted, banned);
        db.Categories.Add(category);
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var receipt = await services.Reports.CreateAsync(
            muted.Id,
            new ReportCreateCommand("post", post.Id, "spam", null),
            ServiceTestSupport.NewAudit(muted.Id));
        Assert.Equal(ReportStatuses.Pending, receipt.Status);

        var bannedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Reports.CreateAsync(
                banned.Id,
                new ReportCreateCommand("post", post.Id, "spam", null),
                ServiceTestSupport.NewAudit(banned.Id)));
        Assert.Equal(ApiErrorCodes.AccountBanned, bannedError.Code);
    }

    [DatabaseFact]
    public async Task Non_admin_cannot_use_moderation_reports_or_user_management()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("governance-member");
        var target = ServiceTestSupport.NewUser("governance-target");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(member.Id, category.Id, ModerationStatuses.Pending);
        var report = new Report
        {
            ReporterId = target.Id,
            TargetType = ReportTargetTypes.Post,
            TargetId = post.Id,
            Reason = ReportReasons.Other,
            Status = ReportStatuses.Pending
        };
        db.Users.AddRange(member, target);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Reports.Add(report);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var queueError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Moderation.GetQueueAsync(member.Id, null, 20, null));
        Assert.Equal(ApiErrorCodes.Forbidden, queueError.Code);

        var reportError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Reports.ListAsync(member.Id, "pending", 20, null));
        Assert.Equal(ApiErrorCodes.Forbidden, reportError.Code);

        var userError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AdminUsers.GetAsync(member.Id, target.Id));
        Assert.Equal(ApiErrorCodes.Forbidden, userError.Code);

        var actionError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Moderation.ExecuteAsync(
                member.Id,
                "post",
                post.Id,
                new ModerationActionCommand("approve", null, 1, null, null),
                ServiceTestSupport.NewAudit(member.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, actionError.Code);
    }

    private static async Task<AdminReportView?> FindReportAsync(
        ReportService reports,
        Guid adminId,
        string status,
        Guid reportId)
    {
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await reports.ListAsync(adminId, status, 50, cursor);
            if (page.InvalidCursor)
            {
                return null;
            }

            var item = page.Items.FirstOrDefault(candidate => candidate.Id == reportId);
            if (item is not null)
            {
                return item;
            }

            if (page.NextCursor is null)
            {
                return null;
            }

            cursor = page.NextCursor;
        }

        return null;
    }
}