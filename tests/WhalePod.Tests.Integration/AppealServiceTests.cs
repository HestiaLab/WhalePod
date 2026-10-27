using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class AppealServiceTests
{
    [DatabaseFact]
    public async Task Rejected_post_appeal_is_submitted_reviewed_and_restored_with_notification_and_email()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        await db.Appeals
            .Where(appeal => appeal.Status == AppealStatuses.Pending)
            .ExecuteDeleteAsync();

        var author = ServiceTestSupport.NewUser("appeal-post-author");
        author.Email = $"appeal-post-{ServiceTestSupport.NewSuffix()}@example.test";
        var admin = ServiceTestSupport.NewUser("appeal-post-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Rejected);
        db.Users.AddRange(author, admin);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.ModerationActions.Add(RejectAction(admin.Id, "post", post.Id, post.Version));
        await db.SaveChangesAsync();
        var service = NewService(db);

        var appeal = await service.SubmitAsync(
            author.Id,
            "post",
            post.Id,
            "这是误判，内容包含可复现步骤。",
            ServiceTestSupport.NewAudit(author.Id));
        Assert.Equal(AppealStatuses.Pending, appeal.Status);
        Assert.Equal(post.Id, appeal.TargetId);

        var duplicate = await Assert.ThrowsAsync<ApiProblemException>(
            () => service.SubmitAsync(
                author.Id,
                "post",
                post.Id,
                "重复申诉",
                ServiceTestSupport.NewAudit(author.Id)));
        Assert.Equal(ApiErrorCodes.Conflict, duplicate.Code);

        var pending = await service.GetPendingAsync(admin.Id);
        var pendingAppeal = Assert.Single(pending, candidate => candidate.Id == appeal.Id);
        Assert.Equal(author.Handle, pendingAppeal.AppellantHandle);
        Assert.Equal("post", pendingAppeal.TargetType);

        var reviewed = await service.ReviewAsync(
            admin.Id,
            appeal.Id,
            new AppealReviewCommand("accepted", "复核确认是误判，恢复内容。"),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(AppealStatuses.Accepted, reviewed.Status);
        Assert.Equal("复核确认是误判，恢复内容。", reviewed.ReviewNote);

        db.ChangeTracker.Clear();
        var storedPost = await db.Posts.SingleAsync(candidate => candidate.Id == post.Id);
        Assert.Equal(ModerationStatuses.Approved, storedPost.ModerationStatus);
        Assert.Equal(2, storedPost.Version);
        Assert.True(await db.ModerationActions.AnyAsync(action =>
            action.TargetType == "post"
            && action.TargetId == post.Id
            && action.Action == ModerationActions.Restore
            && action.ActorId == admin.Id));
        var notification = await db.Notifications.SingleAsync(candidate =>
            candidate.UserId == author.Id && candidate.Type == NotificationTypes.AppealResult);
        var payload = notification.PayloadJson.RootElement;
        Assert.Contains("通过复核", payload.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal($"/posts/{post.Id}", payload.GetProperty("targetUrl").GetString());
        var email = await db.EmailOutbox.SingleAsync(candidate =>
            candidate.UserId == author.Id && candidate.Template == NotificationTypes.AppealResult);
        Assert.Equal(EmailStatuses.Pending, email.Status);
        Assert.Equal(
            EmailPreferenceTypes.ModerationResults,
            email.PayloadJson.RootElement.GetProperty("preferenceType").GetString());
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "appeal.accepted" && log.TargetId == appeal.Id));

        var mine = await service.GetMineAsync(author.Id);
        Assert.Equal(AppealStatuses.Accepted, Assert.Single(mine).Status);
    }

    [DatabaseFact]
    public async Task Rejected_comment_appeal_can_be_declined_without_restoring_content()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("appeal-comment-author");
        author.Email = $"appeal-comment-{ServiceTestSupport.NewSuffix()}@example.test";
        var admin = ServiceTestSupport.NewUser("appeal-comment-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var comment = ServiceTestSupport.NewComment(
            post.Id,
            author.Id,
            ModerationStatuses.Rejected,
            body: "被驳回的评论内容");
        db.Users.AddRange(author, admin);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.Add(comment);
        db.ModerationActions.Add(RejectAction(admin.Id, "comment", comment.Id, comment.Version));
        await db.SaveChangesAsync();
        var service = NewService(db);

        var appeal = await service.SubmitAsync(
            author.Id,
            "comment",
            comment.Id,
            "评论没有违规。",
            ServiceTestSupport.NewAudit(author.Id));
        var reviewed = await service.ReviewAsync(
            admin.Id,
            appeal.Id,
            new AppealReviewCommand("rejected", "复核后维持原判。"),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(AppealStatuses.Rejected, reviewed.Status);

        db.ChangeTracker.Clear();
        var storedComment = await db.Comments.SingleAsync(candidate => candidate.Id == comment.Id);
        Assert.Equal(ModerationStatuses.Rejected, storedComment.ModerationStatus);
        var notification = await db.Notifications.SingleAsync(candidate =>
            candidate.UserId == author.Id && candidate.Type == NotificationTypes.AppealResult);
        Assert.Contains("未通过复核", notification.PayloadJson.RootElement.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            $"/posts/{post.Id}?comment={comment.Id}",
            notification.PayloadJson.RootElement.GetProperty("targetUrl").GetString());
        Assert.True(await db.EmailOutbox.AnyAsync(candidate =>
            candidate.UserId == author.Id && candidate.Template == NotificationTypes.AppealResult));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "appeal.rejected" && log.TargetId == appeal.Id));
    }

    [DatabaseFact]
    public async Task Appeal_submission_and_review_enforce_ownership_state_and_reviewer_rules()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("appeal-guard-author");
        var outsider = ServiceTestSupport.NewUser("appeal-guard-outsider");
        var owner = ServiceTestSupport.NewUser("appeal-guard-owner");
        var admin = ServiceTestSupport.NewUser("appeal-guard-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var approvedPost = ServiceTestSupport.NewPost(owner.Id, category.Id);
        var rejectedPost = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Rejected);
        var adminPost = ServiceTestSupport.NewPost(admin.Id, category.Id, ModerationStatuses.Rejected);
        db.Users.AddRange(author, outsider, owner, admin);
        db.Categories.Add(category);
        db.Posts.AddRange(approvedPost, rejectedPost, adminPost);
        db.ModerationActions.Add(RejectAction(admin.Id, "post", rejectedPost.Id, rejectedPost.Version));
        db.ModerationActions.Add(RejectAction(admin.Id, "post", adminPost.Id, adminPost.Version));
        await db.SaveChangesAsync();
        var service = NewService(db);

        var notOwner = await Assert.ThrowsAsync<ApiProblemException>(
            () => service.SubmitAsync(
                outsider.Id,
                "post",
                rejectedPost.Id,
                "不是作者",
                ServiceTestSupport.NewAudit(outsider.Id)));
        Assert.Equal(ApiErrorCodes.NotFound, notOwner.Code);

        var approved = await Assert.ThrowsAsync<ApiProblemException>(
            () => service.SubmitAsync(
                approvedPost.AuthorId,
                "post",
                approvedPost.Id,
                "未驳回",
                ServiceTestSupport.NewAudit(approvedPost.AuthorId)));
        Assert.Equal(ApiErrorCodes.Conflict, approved.Code);

        var adminAppeal = await service.SubmitAsync(
            admin.Id,
            "post",
            adminPost.Id,
            "管理员自己的申诉",
            ServiceTestSupport.NewAudit(admin.Id));
        var selfReview = await Assert.ThrowsAsync<ApiProblemException>(
            () => service.ReviewAsync(
                admin.Id,
                adminAppeal.Id,
                new AppealReviewCommand("accepted", "自审"),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, selfReview.Code);

        var authorAppeal = await service.SubmitAsync(
            author.Id,
            "post",
            rejectedPost.Id,
            "需要复核",
            ServiceTestSupport.NewAudit(author.Id));
        var memberReview = await Assert.ThrowsAsync<ApiProblemException>(
            () => service.ReviewAsync(
                outsider.Id,
                authorAppeal.Id,
                new AppealReviewCommand("accepted", "普通成员复核"),
                ServiceTestSupport.NewAudit(outsider.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, memberReview.Code);

        var missingNote = await Assert.ThrowsAsync<ApiProblemException>(
            () => service.ReviewAsync(
                admin.Id,
                authorAppeal.Id,
                new AppealReviewCommand("accepted", " "),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, missingNote.Code);
    }

    [DatabaseFact]
    public async Task Account_penalty_appeal_acceptance_restores_account_and_notifies()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser(
            "appeal-penalty-member",
            status: UserStatuses.Muted,
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(7),
            statusReason: "测试禁言");
        member.Email = $"appeal-penalty-{ServiceTestSupport.NewSuffix()}@example.test";
        var admin = ServiceTestSupport.NewUser("appeal-penalty-admin", UserRoles.Admin);
        db.Users.AddRange(member, admin);
        await db.SaveChangesAsync();
        var service = NewService(db);

        var appeal = await service.SubmitAsync(
            member.Id,
            "account_penalty",
            member.Id,
            "禁言依据不足。",
            ServiceTestSupport.NewAudit(member.Id));
        Assert.Equal(AppealTargetTypes.AccountPenalty, appeal.TargetType);
        var reviewed = await service.ReviewAsync(
            admin.Id,
            appeal.Id,
            new AppealReviewCommand("accepted", "复核后解除禁言。"),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(AppealStatuses.Accepted, reviewed.Status);

        db.ChangeTracker.Clear();
        var stored = await db.Users.SingleAsync(user => user.Id == member.Id);
        Assert.Equal(UserStatuses.Active, stored.Status);
        Assert.Null(stored.StatusReason);
        Assert.Null(stored.StatusExpiresAt);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "user.status_changed" && log.TargetId == member.Id));
        var notification = await db.Notifications.SingleAsync(candidate =>
            candidate.UserId == member.Id && candidate.Type == NotificationTypes.AppealResult);
        var payload = notification.PayloadJson.RootElement;
        Assert.Equal("user", payload.GetProperty("targetType").GetString());
        Assert.Contains("账号处罚已解除", payload.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.True(await db.EmailOutbox.AnyAsync(candidate =>
            candidate.UserId == member.Id && candidate.Template == NotificationTypes.AppealResult));
    }

    private static AppealService NewService(WhalePod.Infrastructure.Persistence.WhalePodDbContext db)
    {
        return new AppealService(
            db,
            new ActorGuard(db, TimeProvider.System),
            new EmailNotificationQueue(db),
            TimeProvider.System);
    }

    private static ModerationAction RejectAction(
        Guid adminId,
        string targetType,
        Guid targetId,
        int version)
    {
        return new ModerationAction
        {
            TargetType = targetType,
            TargetId = targetId,
            ActorType = ModerationActorTypes.User,
            ActorId = adminId,
            Action = ModerationActions.Reject,
            FromStatus = ModerationStatuses.Pending,
            ToStatus = ModerationStatuses.Rejected,
            Reason = "测试驳回",
            ContentVersion = version,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}