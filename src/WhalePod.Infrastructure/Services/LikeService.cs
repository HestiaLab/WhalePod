using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Domain.Interactions;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record LikeStateView(bool Liked, int LikeCount);

public sealed class LikeService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    TimeProvider timeProvider)
{
    public async Task<LikeStateView> GetPostLikeStateAsync(
        Guid viewerId,
        Guid postId,
        CancellationToken cancellationToken = default)
    {
        var liked = await db.PostLikes
            .AsNoTracking()
            .AnyAsync(like => like.PostId == postId && like.UserId == viewerId, cancellationToken);
        var likeCount = await db.PostLikes
            .AsNoTracking()
            .CountAsync(like => like.PostId == postId, cancellationToken);
        return new LikeStateView(liked, likeCount);
    }

    public async Task<IReadOnlyDictionary<Guid, bool>> GetCommentLikeStatesAsync(
        Guid viewerId,
        IReadOnlyCollection<Guid> commentIds,
        CancellationToken cancellationToken = default)
    {
        if (commentIds.Count == 0)
        {
            return new Dictionary<Guid, bool>();
        }

        var likedCommentIds = await db.CommentLikes
            .AsNoTracking()
            .Where(like => like.UserId == viewerId && commentIds.Contains(like.CommentId))
            .Select(like => like.CommentId)
            .ToListAsync(cancellationToken);
        var liked = likedCommentIds.ToHashSet();
        return commentIds
            .Distinct()
            .ToDictionary(commentId => commentId, commentId => liked.Contains(commentId));
    }

    public async Task<LikeStateView> SetPostLikeAsync(
        Guid actorId,
        Guid postId,
        bool liked,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireWriteAccessAsync(
            actorId,
            ActorWriteScope.Content,
            cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var post = await db.Posts
            .FromSqlInterpolated($"SELECT * FROM post WHERE id = {postId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (post is null
            || post.ModerationStatus != ModerationStatuses.Approved
            || post.DeletedAt is not null)
        {
            throw ApiProblemException.NotFound("帖子不存在或当前不可点赞。");
        }

        if (liked && post.AuthorId == actor.Id)
        {
            throw ApiProblemException.Forbidden("self_like", "不能点赞自己的帖子。");
        }

        var existing = await db.PostLikes
            .SingleOrDefaultAsync(
                like => like.PostId == postId && like.UserId == actor.Id,
                cancellationToken);
        var changed = false;
        if (liked && existing is null)
        {
            db.PostLikes.Add(new PostLike
            {
                PostId = postId,
                UserId = actor.Id,
                CreatedAt = now
            });
            changed = true;
        }
        else if (!liked && existing is not null)
        {
            db.PostLikes.Remove(existing);
            changed = true;
        }

        if (changed)
        {
            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                liked ? "post.liked" : "post.unliked",
                "post",
                postId,
                new { liked },
                now));
        }

        await db.SaveChangesAsync(cancellationToken);
        var likeCount = await db.PostLikes
            .AsNoTracking()
            .CountAsync(like => like.PostId == postId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LikeStateView(liked, likeCount);
    }

    public async Task<LikeStateView> SetCommentLikeAsync(
        Guid actorId,
        Guid commentId,
        bool liked,
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
            || comment.ModerationStatus != ModerationStatuses.Approved
            || comment.DeletedAt is not null)
        {
            throw ApiProblemException.NotFound("评论不存在或当前不可点赞。");
        }

        var postVisible = await db.Posts
            .AsNoTracking()
            .AnyAsync(post => post.Id == comment.PostId
                && post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null, cancellationToken);
        if (!postVisible)
        {
            throw ApiProblemException.NotFound("评论不存在或当前不可点赞。");
        }

        if (liked && comment.AuthorId == actor.Id)
        {
            throw ApiProblemException.Forbidden("self_like", "不能点赞自己的评论。");
        }

        var existing = await db.CommentLikes
            .SingleOrDefaultAsync(
                like => like.CommentId == commentId && like.UserId == actor.Id,
                cancellationToken);
        var changed = false;
        if (liked && existing is null)
        {
            db.CommentLikes.Add(new CommentLike
            {
                CommentId = commentId,
                UserId = actor.Id,
                CreatedAt = now
            });
            changed = true;
        }
        else if (!liked && existing is not null)
        {
            db.CommentLikes.Remove(existing);
            changed = true;
        }

        var storedCount = await db.CommentLikes
            .AsNoTracking()
            .CountAsync(like => like.CommentId == commentId, cancellationToken);
        var likeCount = Math.Max(0, storedCount + (changed ? (liked ? 1 : -1) : 0));
        comment.LikeCount = likeCount;
        if (changed)
        {
            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                liked ? "comment.liked" : "comment.unliked",
                "comment",
                commentId,
                new { liked, likeCount },
                now));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LikeStateView(liked, comment.LikeCount);
    }
}