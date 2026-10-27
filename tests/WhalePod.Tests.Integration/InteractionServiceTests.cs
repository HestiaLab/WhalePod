using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class InteractionServiceTests
{
    [DatabaseFact]
    public async Task Post_like_is_idempotent_and_rejects_self_and_invisible_targets()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("like-post-author");
        var liker = ServiceTestSupport.NewUser("like-post-member");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var pendingPost = ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            ModerationStatuses.Pending,
            "待审核帖子标题足够长");
        db.Users.AddRange(author, liker);
        db.Categories.Add(category);
        db.Posts.AddRange(post, pendingPost);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var liked = await services.Likes.SetPostLikeAsync(
            liker.Id,
            post.Id,
            true,
            ServiceTestSupport.NewAudit(liker.Id));
        Assert.True(liked.Liked);
        Assert.Equal(1, liked.LikeCount);

        var likedAgain = await services.Likes.SetPostLikeAsync(
            liker.Id,
            post.Id,
            true,
            ServiceTestSupport.NewAudit(liker.Id));
        Assert.True(likedAgain.Liked);
        Assert.Equal(1, likedAgain.LikeCount);
        Assert.Equal(1, await db.PostLikes.CountAsync(like => like.PostId == post.Id));

        var unliked = await services.Likes.SetPostLikeAsync(
            liker.Id,
            post.Id,
            false,
            ServiceTestSupport.NewAudit(liker.Id));
        Assert.False(unliked.Liked);
        Assert.Equal(0, unliked.LikeCount);

        var unlikedAgain = await services.Likes.SetPostLikeAsync(
            liker.Id,
            post.Id,
            false,
            ServiceTestSupport.NewAudit(liker.Id));
        Assert.False(unlikedAgain.Liked);
        Assert.Equal(0, unlikedAgain.LikeCount);
        Assert.Equal(0, await db.PostLikes.CountAsync(like => like.PostId == post.Id));

        var selfLike = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Likes.SetPostLikeAsync(
                author.Id,
                post.Id,
                true,
                ServiceTestSupport.NewAudit(author.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, selfLike.Code);
        var selfLikeDetails = System.Text.Json.JsonSerializer.SerializeToElement(selfLike.Details);
        Assert.Equal("self_like", selfLikeDetails.GetProperty("reason").GetString());

        var invisible = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Likes.SetPostLikeAsync(
                liker.Id,
                pendingPost.Id,
                true,
                ServiceTestSupport.NewAudit(liker.Id)));
        Assert.Equal(ApiErrorCodes.NotFound, invisible.Code);

        Assert.Equal(1, await db.AuditLogs.CountAsync(log =>
            log.Action == "post.liked" && log.TargetId == post.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(log =>
            log.Action == "post.unliked" && log.TargetId == post.Id));
    }

    [DatabaseFact]
    public async Task Comment_like_keeps_cached_count_consistent_and_rejects_self()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("like-comment-author");
        var liker = ServiceTestSupport.NewUser("like-comment-member");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var comment = ServiceTestSupport.NewComment(post.Id, author.Id, body: "作者的公开评论");
        var pendingComment = ServiceTestSupport.NewComment(
            post.Id,
            author.Id,
            ModerationStatuses.Pending,
            body: "待审核评论");
        db.Users.AddRange(author, liker);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.AddRange(comment, pendingComment);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var liked = await services.Likes.SetCommentLikeAsync(
            liker.Id,
            comment.Id,
            true,
            ServiceTestSupport.NewAudit(liker.Id));
        Assert.True(liked.Liked);
        Assert.Equal(1, liked.LikeCount);

        var likedAgain = await services.Likes.SetCommentLikeAsync(
            liker.Id,
            comment.Id,
            true,
            ServiceTestSupport.NewAudit(liker.Id));
        Assert.Equal(1, likedAgain.LikeCount);
        Assert.Equal(1, await db.CommentLikes.CountAsync(like => like.CommentId == comment.Id));

        db.ChangeTracker.Clear();
        var storedComment = await db.Comments.SingleAsync(candidate => candidate.Id == comment.Id);
        Assert.Equal(1, storedComment.LikeCount);

        var publicPage = Assert.IsType<CommentPage>(
            await services.Content.GetCommentsAsync(post.Id, "top", 50, null));
        Assert.Equal(1, Assert.Single(publicPage.Items, item => item.Id == comment.Id).LikeCount);

        var unliked = await services.Likes.SetCommentLikeAsync(
            liker.Id,
            comment.Id,
            false,
            ServiceTestSupport.NewAudit(liker.Id));
        Assert.False(unliked.Liked);
        Assert.Equal(0, unliked.LikeCount);

        db.ChangeTracker.Clear();
        storedComment = await db.Comments.SingleAsync(candidate => candidate.Id == comment.Id);
        Assert.Equal(0, storedComment.LikeCount);
        Assert.Equal(0, await db.CommentLikes.CountAsync(like => like.CommentId == comment.Id));

        var selfLike = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Likes.SetCommentLikeAsync(
                author.Id,
                comment.Id,
                true,
                ServiceTestSupport.NewAudit(author.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, selfLike.Code);

        var invisible = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Likes.SetCommentLikeAsync(
                liker.Id,
                pendingComment.Id,
                true,
                ServiceTestSupport.NewAudit(liker.Id)));
        Assert.Equal(ApiErrorCodes.NotFound, invisible.Code);
    }

    [DatabaseFact]
    public async Task Like_rejects_pending_muted_banned_and_deleted_subjects_but_allows_member_and_admin()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("like-state-author");
        var pending = ServiceTestSupport.NewUser("like-state-pending", activated: false);
        var muted = ServiceTestSupport.NewUser(
            "like-state-muted",
            status: UserStatuses.Muted,
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
            statusReason: "测试禁言");
        var banned = ServiceTestSupport.NewUser(
            "like-state-banned",
            status: UserStatuses.Banned,
            statusReason: "测试封禁");
        var deleted = ServiceTestSupport.NewUser("like-state-deleted");
        deleted.DeletedAt = DateTimeOffset.UtcNow.AddDays(-1);
        deleted.Status = UserStatuses.Deleted;
        var member = ServiceTestSupport.NewUser("like-state-member");
        var admin = ServiceTestSupport.NewUser("like-state-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        db.Users.AddRange(author, pending, muted, banned, deleted, member, admin);
        db.Categories.Add(category);
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);

        var pendingError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Likes.SetPostLikeAsync(
                pending.Id,
                post.Id,
                true,
                ServiceTestSupport.NewAudit(pending.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, pendingError.Code);

        var mutedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Likes.SetPostLikeAsync(
                muted.Id,
                post.Id,
                true,
                ServiceTestSupport.NewAudit(muted.Id)));
        Assert.Equal(ApiErrorCodes.AccountMuted, mutedError.Code);

        var bannedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Likes.SetPostLikeAsync(
                banned.Id,
                post.Id,
                true,
                ServiceTestSupport.NewAudit(banned.Id)));
        Assert.Equal(ApiErrorCodes.AccountBanned, bannedError.Code);

        var deletedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Likes.SetPostLikeAsync(
                deleted.Id,
                post.Id,
                true,
                ServiceTestSupport.NewAudit(deleted.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, deletedError.Code);

        var memberState = await services.Likes.SetPostLikeAsync(
            member.Id,
            post.Id,
            true,
            ServiceTestSupport.NewAudit(member.Id));
        Assert.True(memberState.Liked);
        Assert.Equal(1, memberState.LikeCount);

        var adminState = await services.Likes.SetPostLikeAsync(
            admin.Id,
            post.Id,
            true,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.True(adminState.Liked);
        Assert.Equal(2, adminState.LikeCount);

        Assert.Equal(2, await db.PostLikes.CountAsync(like => like.PostId == post.Id));
        Assert.DoesNotContain(
            await db.PostLikes.AsNoTracking().Select(like => like.UserId).ToListAsync(),
            id => id == pending.Id || id == muted.Id || id == banned.Id || id == deleted.Id);
    }

    [DatabaseFact]
    public async Task Accept_enforces_author_visibility_self_and_single_answer_rules()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("accept-author");
        var commenter = ServiceTestSupport.NewUser("accept-commenter");
        var secondCommenter = ServiceTestSupport.NewUser("accept-second");
        var admin = ServiceTestSupport.NewUser("accept-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var otherPost = ServiceTestSupport.NewPost(commenter.Id, category.Id);
        var first = ServiceTestSupport.NewComment(post.Id, commenter.Id, body: "第一条公开答案");
        var second = ServiceTestSupport.NewComment(post.Id, secondCommenter.Id, body: "第二条公开答案");
        var own = ServiceTestSupport.NewComment(post.Id, author.Id, body: "作者自己的评论");
        var pending = ServiceTestSupport.NewComment(
            post.Id,
            commenter.Id,
            ModerationStatuses.Pending,
            body: "待审核的答案");
        var crossPost = ServiceTestSupport.NewComment(otherPost.Id, commenter.Id, body: "其他帖子的评论");
        db.Users.AddRange(author, commenter, secondCommenter, admin);
        db.Categories.Add(category);
        db.Posts.AddRange(post, otherPost);
        db.Comments.AddRange(first, second, own, pending, crossPost);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);

        var adminDenied = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Acceptance.AcceptAsync(
                admin.Id,
                first.Id,
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, adminDenied.Code);

        var crossPostDenied = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Acceptance.AcceptAsync(
                author.Id,
                crossPost.Id,
                ServiceTestSupport.NewAudit(author.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, crossPostDenied.Code);

        var selfDenied = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Acceptance.AcceptAsync(
                author.Id,
                own.Id,
                ServiceTestSupport.NewAudit(author.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, selfDenied.Code);

        var pendingDenied = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Acceptance.AcceptAsync(
                author.Id,
                pending.Id,
                ServiceTestSupport.NewAudit(author.Id)));
        Assert.Equal(ApiErrorCodes.NotFound, pendingDenied.Code);

        var accepted = await services.Acceptance.AcceptAsync(
            author.Id,
            first.Id,
            ServiceTestSupport.NewAudit(author.Id));
        Assert.Equal(post.Id, accepted.PostId);
        Assert.Equal(first.Id, accepted.AcceptedCommentId);
        Assert.True(accepted.IsSolved);

        var acceptedAgain = await services.Acceptance.AcceptAsync(
            author.Id,
            first.Id,
            ServiceTestSupport.NewAudit(author.Id));
        Assert.Equal(first.Id, acceptedAgain.AcceptedCommentId);
        Assert.Equal(1, await db.Notifications.CountAsync(notification =>
            notification.UserId == commenter.Id
            && notification.Type == NotificationTypes.CommentAccepted));

        var secondDenied = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Acceptance.AcceptAsync(
                author.Id,
                second.Id,
                ServiceTestSupport.NewAudit(author.Id)));
        Assert.Equal(ApiErrorCodes.Conflict, secondDenied.Code);

        var wrongUnaccept = await services.Acceptance.UnacceptAsync(
            author.Id,
            second.Id,
            ServiceTestSupport.NewAudit(author.Id));
        Assert.Equal(first.Id, wrongUnaccept.AcceptedCommentId);
        Assert.True(wrongUnaccept.IsSolved);

        var unaccepted = await services.Acceptance.UnacceptAsync(
            author.Id,
            first.Id,
            ServiceTestSupport.NewAudit(author.Id));
        Assert.Null(unaccepted.AcceptedCommentId);
        Assert.False(unaccepted.IsSolved);

        var unacceptedAgain = await services.Acceptance.UnacceptAsync(
            author.Id,
            first.Id,
            ServiceTestSupport.NewAudit(author.Id));
        Assert.Null(unacceptedAgain.AcceptedCommentId);
        Assert.False(unacceptedAgain.IsSolved);
        Assert.Equal(2, await db.Notifications.CountAsync(notification =>
            notification.UserId == commenter.Id
            && notification.Type == NotificationTypes.CommentAccepted));

        db.ChangeTracker.Clear();
        var storedPost = await db.Posts.SingleAsync(candidate => candidate.Id == post.Id);
        Assert.Null(storedPost.AcceptedCommentId);
        Assert.False(storedPost.IsSolved);
        var storedComment = await db.Comments.SingleAsync(candidate => candidate.Id == first.Id);
        Assert.False(storedComment.IsAccepted);

        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "comment.accepted" && log.TargetId == first.Id));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "comment.unaccepted" && log.TargetId == first.Id));
    }

    [DatabaseFact]
    public async Task Accepted_comment_can_be_deleted_with_current_version_and_clears_solution()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("accept-delete-author");
        var commenter = ServiceTestSupport.NewUser("accept-delete-commenter");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var comment = ServiceTestSupport.NewComment(post.Id, commenter.Id, body: "被采纳后删除的评论");
        db.Users.AddRange(author, commenter);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.Add(comment);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        await services.Acceptance.AcceptAsync(
            author.Id,
            comment.Id,
            ServiceTestSupport.NewAudit(author.Id));

        var result = await services.Comments.DeleteAsync(
            commenter.Id,
            comment.Id,
            2,
            null,
            ServiceTestSupport.NewAudit(commenter.Id));
        Assert.True(result.Deleted);
        Assert.True(result.AcceptedCleared);

        db.ChangeTracker.Clear();
        var storedPost = await db.Posts.SingleAsync(candidate => candidate.Id == post.Id);
        Assert.Null(storedPost.AcceptedCommentId);
        Assert.False(storedPost.IsSolved);
        var storedComment = await db.Comments.SingleAsync(candidate => candidate.Id == comment.Id);
        Assert.False(storedComment.IsAccepted);
        Assert.Equal(ModerationStatuses.Deleted, storedComment.ModerationStatus);
    }
}