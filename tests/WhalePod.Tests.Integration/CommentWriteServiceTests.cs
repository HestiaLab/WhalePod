using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class CommentWriteServiceTests
{
    [DatabaseFact]
    public async Task Create_edit_and_parent_validation_follow_moderation_and_version_rules()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("comment-author");
        var commenter = ServiceTestSupport.NewUser("comment-commenter");
        var admin = ServiceTestSupport.NewUser("comment-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var otherPost = ServiceTestSupport.NewPost(author.Id, category.Id);
        db.Users.AddRange(author, commenter, admin);
        db.Categories.Add(category);
        db.Posts.AddRange(post, otherPost);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var comment = await services.Comments.CreateAsync(
            commenter.Id,
            post.Id,
            new CommentCreateCommand("这是待审评论内容", null),
            ServiceTestSupport.NewAudit(commenter.Id));
        Assert.Equal(ModerationStatuses.Pending, comment.ModerationStatus);
        Assert.Equal(1, comment.Version);

        var publicPage = Assert.IsType<CommentPage>(
            await services.Content.GetCommentsAsync(post.Id, "latest", 50, null));
        Assert.DoesNotContain(publicPage.Items, item => item.Id == comment.Id);

        var edited = await services.Comments.UpdateAsync(
            commenter.Id,
            comment.Id,
            1,
            new CommentUpdateCommand("这是编辑后的评论内容", null),
            ServiceTestSupport.NewAudit(commenter.Id));
        Assert.Equal(2, edited.Version);
        Assert.Equal(ModerationStatuses.Pending, edited.ModerationStatus);

        var stale = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Comments.UpdateAsync(
                commenter.Id,
                comment.Id,
                1,
                new CommentUpdateCommand("使用旧版本编辑评论内容", null),
                ServiceTestSupport.NewAudit(commenter.Id)));
        Assert.Equal(ApiErrorCodes.Conflict, stale.Code);

        var adminMissingReason = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Comments.UpdateAsync(
                admin.Id,
                comment.Id,
                2,
                new CommentUpdateCommand("管理员编辑评论内容", null),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, adminMissingReason.Code);

        var adminEdited = await services.Comments.UpdateAsync(
            admin.Id,
            comment.Id,
            2,
            new CommentUpdateCommand("管理员编辑评论内容", "修正不当措辞"),
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(3, adminEdited.Version);
        Assert.Equal(ModerationStatuses.Pending, adminEdited.ModerationStatus);

        var otherPostComment = ServiceTestSupport.NewComment(otherPost.Id, author.Id);
        var pendingParent = ServiceTestSupport.NewComment(post.Id, author.Id, ModerationStatuses.Pending);
        db.Comments.AddRange(otherPostComment, pendingParent);
        await db.SaveChangesAsync();

        var wrongParent = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Comments.CreateAsync(
                commenter.Id,
                post.Id,
                new CommentCreateCommand("回复不属于本帖的评论", otherPostComment.Id),
                ServiceTestSupport.NewAudit(commenter.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, wrongParent.Code);

        var hiddenParent = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Comments.CreateAsync(
                commenter.Id,
                post.Id,
                new CommentCreateCommand("回复尚未公开的评论", pendingParent.Id),
                ServiceTestSupport.NewAudit(commenter.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, hiddenParent.Code);

        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "comment.updated"
            && log.TargetId == comment.Id
            && log.ActorId == admin.Id
            && log.MetadataJson.RootElement.GetProperty("reason").GetString() == "修正不当措辞"));
    }

    [DatabaseFact]
    public async Task Delete_accepted_comment_rolls_back_acceptance_and_keeps_deleted_parent_placeholder()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("comment-accept-author");
        var commenter = ServiceTestSupport.NewUser("comment-accept-commenter");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var accepted = ServiceTestSupport.NewComment(post.Id, commenter.Id, body: "被采纳的评论答案");
        accepted.IsAccepted = true;
        post.AcceptedCommentId = accepted.Id;
        post.IsSolved = true;
        var child = ServiceTestSupport.NewComment(post.Id, author.Id, parentId: accepted.Id, body: "子评论仍然保留");
        db.Users.AddRange(author, commenter);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.AddRange(accepted, child);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var result = await services.Comments.DeleteAsync(
            commenter.Id,
            accepted.Id,
            1,
            null,
            ServiceTestSupport.NewAudit(commenter.Id));
        Assert.True(result.Deleted);
        Assert.True(result.AcceptedCleared);

        db.ChangeTracker.Clear();
        var storedComment = await db.Comments.SingleAsync(comment => comment.Id == accepted.Id);
        Assert.Equal(ModerationStatuses.Deleted, storedComment.ModerationStatus);
        Assert.False(storedComment.IsAccepted);
        Assert.NotNull(storedComment.DeletedAt);
        var storedPost = await db.Posts.SingleAsync(candidate => candidate.Id == post.Id);
        Assert.Null(storedPost.AcceptedCommentId);
        Assert.False(storedPost.IsSolved);
        Assert.True(storedPost.Version > 1);

        var page = Assert.IsType<CommentPage>(
            await services.Content.GetCommentsAsync(post.Id, "latest", 50, null));
        var placeholder = Assert.Single(page.Items, item => item.Id == accepted.Id);
        Assert.True(placeholder.IsDeleted);
        Assert.Null(placeholder.BodyMd);
        Assert.Contains(page.Items, item => item.Id == child.Id);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "post.acceptance_cleared" && log.TargetId == post.Id));
    }

    [DatabaseFact]
    public async Task Comment_writes_reject_pending_muted_banned_and_non_owner_subjects()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("comment-state-author");
        var pending = ServiceTestSupport.NewUser("comment-state-pending", activated: false);
        var muted = ServiceTestSupport.NewUser(
            "comment-state-muted",
            status: UserStatuses.Muted,
            statusReason: "测试禁言",
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1));
        var banned = ServiceTestSupport.NewUser(
            "comment-state-banned",
            status: UserStatuses.Banned,
            statusReason: "测试封禁");
        var outsider = ServiceTestSupport.NewUser("comment-state-outsider");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var comment = ServiceTestSupport.NewComment(post.Id, author.Id, body: "作者自己的评论");
        db.Users.AddRange(author, pending, muted, banned, outsider);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.Add(comment);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var pendingError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Comments.CreateAsync(
                pending.Id,
                post.Id,
                new CommentCreateCommand("待激活用户评论", null),
                ServiceTestSupport.NewAudit(pending.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, pendingError.Code);

        var mutedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Comments.CreateAsync(
                muted.Id,
                post.Id,
                new CommentCreateCommand("禁言用户评论", null),
                ServiceTestSupport.NewAudit(muted.Id)));
        Assert.Equal(ApiErrorCodes.AccountMuted, mutedError.Code);

        var bannedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Comments.CreateAsync(
                banned.Id,
                post.Id,
                new CommentCreateCommand("封禁用户评论", null),
                ServiceTestSupport.NewAudit(banned.Id)));
        Assert.Equal(ApiErrorCodes.AccountBanned, bannedError.Code);

        var forbidden = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Comments.DeleteAsync(
                outsider.Id,
                comment.Id,
                1,
                null,
                ServiceTestSupport.NewAudit(outsider.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, forbidden.Code);

        var adminMissingReason = await Assert.ThrowsAsync<ApiProblemException>(
            () =>
            {
                var admin = ServiceTestSupport.NewUser("comment-state-admin", UserRoles.Admin);
                db.Users.Add(admin);
                db.SaveChanges();
                return services.Comments.DeleteAsync(
                    admin.Id,
                    comment.Id,
                    1,
                    null,
                    ServiceTestSupport.NewAudit(admin.Id));
            });
        Assert.Equal(ApiErrorCodes.ValidationFailed, adminMissingReason.Code);
    }
}