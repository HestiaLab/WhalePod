using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record AcceptResultView(Guid PostId, Guid? AcceptedCommentId, bool IsSolved);

public sealed class AcceptanceService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    EmailNotificationQueue emailQueue,
    TimeProvider timeProvider)
{
    public async Task<AcceptResultView> AcceptAsync(
        Guid actorId,
        Guid commentId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireWriteAccessAsync(
            actorId,
            ActorWriteScope.Content,
            cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var comment = await db.Comments
            .FromSqlInterpolated($"SELECT * FROM comment WHERE id = {commentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (comment is null
            || comment.DeletedAt is not null
            || comment.ModerationStatus != ModerationStatuses.Approved)
        {
            throw ApiProblemException.NotFound("评论不存在或当前不可采纳。");
        }

        var post = await db.Posts
            .FromSqlInterpolated($"SELECT * FROM post WHERE id = {comment.PostId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (post is null
            || post.DeletedAt is not null
            || post.ModerationStatus != ModerationStatuses.Approved)
        {
            throw ApiProblemException.NotFound("帖子不存在或当前不可采纳答案。");
        }

        if (post.AuthorId != actor.Id)
        {
            throw ApiProblemException.Forbidden("not_post_author", "只有帖子作者可以采纳答案。");
        }

        if (post.AcceptedCommentId == comment.Id)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AcceptResultView(post.Id, post.AcceptedCommentId, post.IsSolved);
        }

        if (comment.AuthorId == actor.Id)
        {
            throw ApiProblemException.Forbidden("self_accept", "不能采纳自己的评论。");
        }

        if (post.AcceptedCommentId is not null)
        {
            throw ApiProblemException.Conflict(
                "该帖子已有采纳答案，请先取消采纳。",
                new { acceptedCommentId = post.AcceptedCommentId });
        }

        comment.IsAccepted = true;
        comment.Version++;
        comment.UpdatedAt = now;
        post.AcceptedCommentId = comment.Id;
        post.IsSolved = true;
        post.LastActivityAt = now;
        post.Version++;
        post.UpdatedAt = now;

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "comment.accepted",
            "comment",
            comment.Id,
            new
            {
                postId = post.Id,
                commentVersion = comment.Version,
                postVersion = post.Version
            },
            now));

        var notificationPayload = new
        {
            summary = "你的评论被采纳为答案。",
            targetType = "comment",
            targetId = comment.Id,
            targetUrl = $"/posts/{post.Id}?comment={comment.Id}",
            postId = post.Id,
            action = "accepted"
        };
        var dedupeKey = $"comment-accepted:{comment.Id:N}:{post.Version}";
        db.Notifications.Add(NotificationWriter.Create(
            comment.AuthorId,
            NotificationTypes.CommentAccepted,
            notificationPayload,
            dedupeKey,
            now));
        await emailQueue.EnqueueForNotificationAsync(
            comment.AuthorId,
            NotificationTypes.CommentAccepted,
            notificationPayload,
            dedupeKey,
            now,
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcceptResultView(post.Id, post.AcceptedCommentId, post.IsSolved);
    }

    public async Task<AcceptResultView> UnacceptAsync(
        Guid actorId,
        Guid commentId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireWriteAccessAsync(
            actorId,
            ActorWriteScope.Content,
            cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var comment = await db.Comments
            .FromSqlInterpolated($"SELECT * FROM comment WHERE id = {commentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (comment is null || comment.DeletedAt is not null)
        {
            throw ApiProblemException.NotFound("评论不存在或不可取消采纳。");
        }

        var post = await db.Posts
            .FromSqlInterpolated($"SELECT * FROM post WHERE id = {comment.PostId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (post is null || post.DeletedAt is not null)
        {
            throw ApiProblemException.NotFound("帖子不存在或不可取消采纳。");
        }

        if (post.AuthorId != actor.Id)
        {
            throw ApiProblemException.Forbidden("not_post_author", "只有帖子作者可以取消采纳。");
        }

        if (post.AcceptedCommentId != comment.Id)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AcceptResultView(post.Id, post.AcceptedCommentId, post.IsSolved);
        }

        comment.IsAccepted = false;
        comment.Version++;
        comment.UpdatedAt = now;
        post.AcceptedCommentId = null;
        post.IsSolved = false;
        post.LastActivityAt = now;
        post.Version++;
        post.UpdatedAt = now;

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "comment.unaccepted",
            "comment",
            comment.Id,
            new
            {
                postId = post.Id,
                commentVersion = comment.Version,
                postVersion = post.Version
            },
            now));

        if (comment.AuthorId != actor.Id)
        {
            var notificationPayload = new
            {
                summary = "你的评论已被取消采纳。",
                targetType = "comment",
                targetId = comment.Id,
                targetUrl = $"/posts/{post.Id}?comment={comment.Id}",
                postId = post.Id,
                action = "unaccepted"
            };
            var dedupeKey = $"comment-accepted:{comment.Id:N}:{post.Version}:unaccepted";
            db.Notifications.Add(NotificationWriter.Create(
                comment.AuthorId,
                NotificationTypes.CommentAccepted,
                notificationPayload,
                dedupeKey,
                now));
            await emailQueue.EnqueueForNotificationAsync(
                comment.AuthorId,
                NotificationTypes.CommentAccepted,
                notificationPayload,
                dedupeKey,
                now,
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcceptResultView(post.Id, post.AcceptedCommentId, post.IsSolved);
    }
}