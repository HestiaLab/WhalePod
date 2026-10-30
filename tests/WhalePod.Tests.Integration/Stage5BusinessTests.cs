using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class Stage5BusinessTests
{
    [DatabaseFact]
    public async Task Editing_accepted_comment_clears_acceptance_and_activity_updates_on_accept()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("stage5-accept-author");
        var commenter = ServiceTestSupport.NewUser("stage5-accept-commenter");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var comment = ServiceTestSupport.NewComment(post.Id, commenter.Id, body: "即将被采纳的评论");
        var second = ServiceTestSupport.NewComment(post.Id, commenter.Id, body: "第二个评论用于取消采纳");
        db.Users.AddRange(author, commenter);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.AddRange(comment, second);
        await db.SaveChangesAsync();
        var originalActivity = post.LastActivityAt;

        var services = ServiceTestSupport.CreateServices(db);
        await services.Acceptance.AcceptAsync(
            author.Id,
            comment.Id,
            ServiceTestSupport.NewAudit(author.Id));
        var acceptedActivity = await db.Posts.AsNoTracking()
            .Where(candidate => candidate.Id == post.Id)
            .Select(candidate => candidate.LastActivityAt)
            .SingleAsync();
        Assert.True(acceptedActivity > originalActivity);

        await using (var editDb = ServiceTestSupport.CreateContext())
        {
            var editServices = ServiceTestSupport.CreateServices(editDb);
            var currentVersion = await editDb.Comments.AsNoTracking()
                .Where(candidate => candidate.Id == comment.Id)
                .Select(candidate => candidate.Version)
                .SingleAsync();
            var edited = await editServices.Comments.UpdateAsync(
                commenter.Id,
                comment.Id,
                currentVersion,
                new CommentUpdateCommand("编辑已采纳评论并重新提交审核", null),
                ServiceTestSupport.NewAudit(commenter.Id));
            Assert.Equal(ModerationStatuses.Pending, edited.ModerationStatus);
        }

        db.ChangeTracker.Clear();
        var storedPost = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == post.Id);
        var storedComment = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == comment.Id);
        Assert.Null(storedPost.AcceptedCommentId);
        Assert.False(storedPost.IsSolved);
        Assert.False(storedComment.IsAccepted);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.TargetId == post.Id && log.Action == "post.acceptance_cleared"));
        Assert.True(await db.Notifications.AnyAsync(notification =>
            notification.UserId == author.Id
            && notification.Type == NotificationTypes.CommentAccepted));

        await services.Acceptance.AcceptAsync(
            author.Id,
            second.Id,
            ServiceTestSupport.NewAudit(author.Id));
        var beforeUnaccept = await db.Posts.AsNoTracking()
            .Where(candidate => candidate.Id == post.Id)
            .Select(candidate => candidate.LastActivityAt)
            .SingleAsync();
        await services.Acceptance.UnacceptAsync(
            author.Id,
            second.Id,
            ServiceTestSupport.NewAudit(author.Id));
        var afterUnaccept = await db.Posts.AsNoTracking()
            .Where(candidate => candidate.Id == post.Id)
            .Select(candidate => candidate.LastActivityAt)
            .SingleAsync();
        Assert.True(afterUnaccept >= beforeUnaccept);
    }

    [DatabaseFact]
    public async Task Appeal_queue_paginates_and_deleted_target_can_be_rejected()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var admin = ServiceTestSupport.NewUser("stage5-appeal-admin", UserRoles.Admin);
        var appellant = ServiceTestSupport.NewUser("stage5-appeal-appellant");
        db.Users.AddRange(admin, appellant);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 51; index++)
        {
            db.Appeals.Add(new Appeal
            {
                AppellantId = appellant.Id,
                TargetType = AppealTargetTypes.Post,
                TargetId = Guid.NewGuid(),
                Reason = $"队列分页申诉 {index}",
                Status = AppealStatuses.Pending,
                CreatedAt = now.AddMinutes(-index)
            });
        }

        await db.SaveChangesAsync();
        var service = NewAppealService(db);
        var firstPage = await service.GetPendingAsync(admin.Id, 1);
        var secondPage = await service.GetPendingAsync(admin.Id, 2);
        Assert.Equal(50, firstPage.Count);
        Assert.NotEmpty(secondPage);
        var firstPageIds = firstPage.Select(appeal => appeal.Id).ToHashSet();
        Assert.DoesNotContain(secondPage[0].Id, firstPageIds);

        var author = ServiceTestSupport.NewUser("stage5-appeal-author");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Rejected);
        db.Users.Add(author);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.ModerationActions.Add(RejectAction(admin.Id, "post", post.Id, post.Version));
        await db.SaveChangesAsync();
        var appeal = await service.SubmitAsync(
            author.Id,
            "post",
            post.Id,
            "目标随后被删除的申诉",
            ServiceTestSupport.NewAudit(author.Id));
        post.DeletedAt = now;
        await db.SaveChangesAsync();

        var rejected = await service.ReviewAsync(
            admin.Id,
            appeal.Id,
            new AppealReviewCommand("rejected", "目标已删除，驳回结案。"),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(AppealStatuses.Rejected, rejected.Status);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.TargetId == appeal.Id && log.Action == "appeal.rejected"));
    }

    [DatabaseFact]
    public async Task Appeal_review_note_is_limited_to_the_stored_reason_length()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("stage5-note-author");
        var admin = ServiceTestSupport.NewUser("stage5-note-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Rejected);
        db.Users.AddRange(author, admin);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.ModerationActions.Add(RejectAction(admin.Id, "post", post.Id, post.Version));
        await db.SaveChangesAsync();
        var service = NewAppealService(db);
        var appeal = await service.SubmitAsync(
            author.Id,
            "post",
            post.Id,
            "复核说明长度边界测试",
            ServiceTestSupport.NewAudit(author.Id));

        var tooLong = await Assert.ThrowsAsync<ApiProblemException>(
            () => service.ReviewAsync(
                admin.Id,
                appeal.Id,
                new AppealReviewCommand("accepted", new string('a', 501)),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, tooLong.Code);

        var reviewed = await service.ReviewAsync(
            admin.Id,
            appeal.Id,
            new AppealReviewCommand("accepted", new string('a', 500)),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(AppealStatuses.Accepted, reviewed.Status);
    }

    [DatabaseFact]
    public async Task Sensitive_word_reject_and_manual_policies_are_enforced()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("stage5-word-member");
        var admin = ServiceTestSupport.NewUser("stage5-word-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var blockedTerm = $"blocked-{suffix}";
        var manualTerm = $"manual-{suffix}";
        db.Users.AddRange(member, admin);
        db.Categories.Add(category);
        db.SensitiveWords.AddRange(
            new SensitiveWord
            {
                Term = blockedTerm,
                Action = SensitiveWordActions.Reject,
                Status = SensitiveWordStatuses.Active,
                CreatedBy = admin.Id
            },
            new SensitiveWord
            {
                Term = manualTerm,
                Action = SensitiveWordActions.Manual,
                Status = SensitiveWordStatuses.Active,
                CreatedBy = admin.Id
            });
        var policy = await db.SiteSettings
            .SingleOrDefaultAsync(setting => setting.Key == SensitiveWordAdminService.PolicyKey);
        if (policy is null)
        {
            policy = new SiteSetting
            {
                Key = SensitiveWordAdminService.PolicyKey,
                Value = JsonDocument.Parse("{\"hit\":\"reject\"}")
            };
            db.SiteSettings.Add(policy);
        }
        else
        {
            policy.Value = JsonDocument.Parse("{\"hit\":\"reject\"}");
        }

        await db.SaveChangesAsync();
        var services = ServiceTestSupport.CreateServices(db);

        var rejected = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.CreateAsync(
                member.Id,
                new PostCreateCommand(
                    $"包含敏感词的标题 {suffix}",
                    category.Id,
                    $"这是正文，包含 {blockedTerm} 内容。",
                    null,
                    null,
                    null,
                    null),
                ServiceTestSupport.NewAudit(member.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, rejected.Code);
        Assert.False(await db.Posts.AnyAsync(post =>
            post.AuthorId == member.Id && post.Title.Contains(blockedTerm)));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.ActorId == member.Id && log.Action == "sensitive_word.rejected"));

        policy.Value = JsonDocument.Parse("{\"hit\":\"manual\"}");
        await db.SaveChangesAsync();
        var created = await services.Posts.CreateAsync(
            member.Id,
            new PostCreateCommand(
                $"包含人工审核词的标题 {suffix}",
                category.Id,
                $"这是正文，包含 {manualTerm} 内容。",
                null,
                null,
                null,
                null),
            ServiceTestSupport.NewAudit(member.Id));
        Assert.Equal(ModerationStatuses.Pending, created.ModerationStatus);
        Assert.True(await db.ModerationChecks.AnyAsync(check =>
            check.TargetType == "post"
            && check.TargetId == created.Id
            && check.Provider == "local-sensitive-word"
            && check.Result == ModerationCheckResults.Review));
    }

    [DatabaseFact]
    public async Task Category_kernel_requirement_follows_the_explicit_policy_not_the_display_name()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("stage5-category-member");
        var namedHomework = ServiceTestSupport.NewCategory("抄作业");
        namedHomework.RequiresKernel = false;
        var flagged = ServiceTestSupport.NewCategory("普通板块");
        flagged.RequiresKernel = true;
        db.Users.Add(member);
        db.Categories.AddRange(namedHomework, flagged);
        await db.SaveChangesAsync();
        var services = ServiceTestSupport.CreateServices(db);

        var allowed = await services.Posts.CreateAsync(
            member.Id,
            new PostCreateCommand(
                "名称是抄作业但不要求内核",
                namedHomework.Id,
                "正文长度足够且不包含内核信息。",
                null,
                null,
                null,
                null),
            ServiceTestSupport.NewAudit(member.Id));
        Assert.Equal(ModerationStatuses.Pending, allowed.ModerationStatus);

        var denied = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Posts.CreateAsync(
                member.Id,
                new PostCreateCommand(
                    "普通名称但要求内核",
                    flagged.Id,
                    "正文长度足够但不包含内核信息。",
                    null,
                    null,
                    null,
                    null),
                ServiceTestSupport.NewAudit(member.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, denied.Code);
    }

    [DatabaseFact]
    public async Task Moderation_reject_of_accepted_comment_clears_acceptance_and_approval_updates_activity()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("stage5-moderation-author");
        var commenter = ServiceTestSupport.NewUser("stage5-moderation-commenter");
        var admin = ServiceTestSupport.NewUser("stage5-moderation-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var accepted = ServiceTestSupport.NewComment(post.Id, commenter.Id, body: "被采纳后又被驳回的评论");
        var pending = ServiceTestSupport.NewComment(
            post.Id,
            commenter.Id,
            ModerationStatuses.Pending,
            body: "待审核评论用于验证活跃时间");
        db.Users.AddRange(author, commenter, admin);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.AddRange(accepted, pending);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        await services.Acceptance.AcceptAsync(
            author.Id,
            accepted.Id,
            ServiceTestSupport.NewAudit(author.Id));
        var activityAfterAccept = await db.Posts.AsNoTracking()
            .Where(candidate => candidate.Id == post.Id)
            .Select(candidate => candidate.LastActivityAt)
            .SingleAsync();
        var acceptedVersion = await db.Comments.AsNoTracking()
            .Where(candidate => candidate.Id == accepted.Id)
            .Select(candidate => candidate.Version)
            .SingleAsync();

        await services.Moderation.ExecuteAsync(
            admin.Id,
            "comment",
            accepted.Id,
            new ModerationActionCommand(
                ModerationActions.Hide,
                "复核后隐藏",
                acceptedVersion,
                null,
                null),
            ServiceTestSupport.NewAudit(admin.Id));

        db.ChangeTracker.Clear();
        var storedPost = await db.Posts.AsNoTracking().SingleAsync(candidate => candidate.Id == post.Id);
        var storedAccepted = await db.Comments.AsNoTracking().SingleAsync(candidate => candidate.Id == accepted.Id);
        Assert.False(storedPost.IsSolved);
        Assert.Null(storedPost.AcceptedCommentId);
        Assert.False(storedAccepted.IsAccepted);

        await services.Moderation.ExecuteAsync(
            admin.Id,
            "comment",
            pending.Id,
            new ModerationActionCommand(
                ModerationActions.Approve,
                null,
                pending.Version,
                null,
                null),
            ServiceTestSupport.NewAudit(admin.Id));
        var activityAfterApprove = await db.Posts.AsNoTracking()
            .Where(candidate => candidate.Id == post.Id)
            .Select(candidate => candidate.LastActivityAt)
            .SingleAsync();
        Assert.True(activityAfterApprove >= activityAfterAccept);
    }

    [DatabaseFact]
    public async Task Banned_author_loses_privileged_read_of_own_pending_content()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("stage5-banned-author");
        var category = ServiceTestSupport.NewCategory();
        var pendingPost = ServiceTestSupport.NewPost(author.Id, category.Id, ModerationStatuses.Pending);
        var pendingComment = ServiceTestSupport.NewComment(
            pendingPost.Id,
            author.Id,
            ModerationStatuses.Pending,
            body: "作者待审评论");
        db.Users.Add(author);
        db.Categories.Add(category);
        db.Posts.Add(pendingPost);
        db.Comments.Add(pendingComment);
        await db.SaveChangesAsync();
        var content = new PublicContentService(db);

        Assert.NotNull(await content.GetPostForViewerAsync(pendingPost.Id, author.Id, false));
        Assert.NotNull(await content.GetCommentForViewerAsync(pendingComment.Id, author.Id, false));

        var stored = await db.Users.SingleAsync(user => user.Id == author.Id);
        stored.Status = UserStatuses.Banned;
        stored.StatusReason = "封禁后不得读取自己的待审内容";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Null(await content.GetPostForViewerAsync(pendingPost.Id, author.Id, false));
        Assert.Null(await content.GetCommentForViewerAsync(pendingComment.Id, author.Id, false));
    }

    [DatabaseFact]
    public async Task Comment_moderation_notification_links_to_the_post_and_comment()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("stage6-notify-author");
        var commenter = ServiceTestSupport.NewUser("stage6-notify-commenter");
        var admin = ServiceTestSupport.NewUser("stage6-notify-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var comment = ServiceTestSupport.NewComment(
            post.Id,
            commenter.Id,
            ModerationStatuses.Pending,
            body: "待审核评论用于通知链接测试");
        db.Users.AddRange(author, commenter, admin);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        var services = ServiceTestSupport.CreateServices(db);

        await services.Moderation.ExecuteAsync(
            admin.Id,
            "comment",
            comment.Id,
            new ModerationActionCommand(
                ModerationActions.Approve,
                null,
                comment.Version,
                null,
                null),
            ServiceTestSupport.NewAudit(admin.Id));

        var notification = await db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.UserId == commenter.Id
                && candidate.Type == NotificationTypes.CommentApproved)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstAsync();
        Assert.Equal(
            $"/posts/{post.Id}?comment={comment.Id}",
            notification.PayloadJson.RootElement.GetProperty("targetUrl").GetString());
        Assert.Equal(
            post.Id,
            notification.PayloadJson.RootElement.GetProperty("postId").GetGuid());
    }

    private static AppealService NewAppealService(WhalePodDbContext db)
    {
        return new AppealService(
            db,
            new ActorGuard(db, TimeProvider.System),
            new EmailNotificationQueue(db),
            TimeProvider.System);
    }

    private static ModerationAction RejectAction(Guid adminId, string targetType, Guid targetId, int version)
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