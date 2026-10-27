using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record CommentCreateCommand(string? BodyMd, Guid? ParentId);

public sealed record CommentUpdateCommand(string? BodyMd, string? Reason);

public sealed record CommentDeleteResult(
    Guid Id,
    Guid PostId,
    bool Deleted,
    bool AcceptedCleared);

public sealed class CommentWriteService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    PublicContentService publicContent,
    EmailNotificationQueue emailQueue,
    TimeProvider timeProvider)
{
    public async Task<CommentView> CreateAsync(
        Guid actorId,
        Guid postId,
        CommentCreateCommand command,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireWriteAccessAsync(
            actorId,
            ActorWriteScope.Content,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var bodyMd = NormalizeBody(command.BodyMd);
        var rendered = MarkdownRenderer.Render(bodyMd);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var postExists = await db.Posts
            .AsNoTracking()
            .AnyAsync(post => post.Id == postId
                && post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null, cancellationToken);
        if (!postExists)
        {
            throw ApiProblemException.NotFound("帖子不存在或当前不可评论。");
        }

        Guid? parentAuthorId = null;
        if (command.ParentId is not null)
        {
            var parent = await db.Comments
                .AsNoTracking()
                .SingleOrDefaultAsync(comment => comment.Id == command.ParentId.Value, cancellationToken);
            if (parent is null
                || parent.PostId != postId
                || parent.ModerationStatus != ModerationStatuses.Approved
                || parent.DeletedAt is not null)
            {
                throw ApiProblemException.Validation("parentId", "父评论必须属于同一帖子且当前可见。");
            }

            parentAuthorId = parent.AuthorId;
        }

        var comment = new Comment
        {
            PostId = postId,
            AuthorId = actor.Id,
            ParentId = command.ParentId,
            BodyMd = bodyMd,
            BodyHtml = rendered.Html,
            ModerationStatus = ModerationStatuses.Pending,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Comments.Add(comment);
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "comment.created",
            "comment",
            comment.Id,
            new { postId, parentId = comment.ParentId, moderationStatus = comment.ModerationStatus },
            now));

        if (parentAuthorId is not null && parentAuthorId != actor.Id)
        {
            var notificationPayload = new
            {
                summary = "有人回复了你的评论。",
                targetType = "comment",
                targetId = comment.Id,
                targetUrl = $"/posts/{postId}?comment={comment.Id}",
                postId
            };
            var dedupeKey = $"reply:{comment.Id:N}";
            db.Notifications.Add(NotificationWriter.Create(
                parentAuthorId.Value,
                NotificationTypes.Reply,
                notificationPayload,
                dedupeKey,
                now));
            await emailQueue.EnqueueForNotificationAsync(
                parentAuthorId.Value,
                NotificationTypes.Reply,
                notificationPayload,
                dedupeKey,
                now,
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        var view = await publicContent.GetCommentForViewerAsync(
            comment.Id,
            actorId,
            actor.Role == UserRoles.Admin,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return view ?? throw new InvalidOperationException("Created comment could not be reloaded.");
    }

    public async Task<CommentView> UpdateAsync(
        Guid actorId,
        Guid commentId,
        int expectedVersion,
        CommentUpdateCommand command,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actor = await actorGuard.RequireWriteAccessAsync(
            actorId,
            ActorWriteScope.Content,
            cancellationToken);
        var bodyMd = NormalizeBody(command.BodyMd);
        var rendered = MarkdownRenderer.Render(bodyMd);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var comment = await db.Comments
            .SingleOrDefaultAsync(candidate => candidate.Id == commentId && candidate.DeletedAt == null, cancellationToken);
        if (comment is null)
        {
            throw ApiProblemException.NotFound("评论不存在或不可见。");
        }

        var isAdmin = actor.Role == UserRoles.Admin;
        var isOwner = comment.AuthorId == actorId;
        if (!isOwner && !isAdmin)
        {
            throw ApiProblemException.Forbidden("not_owner", "只能编辑自己的评论。");
        }

        if (!isAdmin && comment.ModerationStatus == ModerationStatuses.Hidden)
        {
            throw ApiProblemException.NotFound("评论不存在或不可见。");
        }

        if (comment.Version != expectedVersion)
        {
            throw VersionConflict(expectedVersion, comment.Version);
        }

        if (isAdmin && !isOwner && string.IsNullOrWhiteSpace(command.Reason))
        {
            throw ApiProblemException.Validation("reason", "管理员编辑他人评论时必须填写原因。");
        }

        if (isAdmin && !isOwner && RuneHelper.Count(command.Reason) > 500)
        {
            throw ApiProblemException.Validation("reason", "原因不能超过 500 个字符。");
        }

        comment.BodyMd = bodyMd;
        comment.BodyHtml = rendered.Html;
        comment.Version++;
        comment.UpdatedAt = now;
        if (!isAdmin)
        {
            comment.ModerationStatus = ModerationStatuses.Pending;
        }

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "comment.updated",
            "comment",
            comment.Id,
            new
            {
                version = comment.Version,
                moderationStatus = comment.ModerationStatus,
                editedByAdmin = isAdmin && !isOwner,
                reason = isAdmin && !isOwner ? command.Reason : null
            },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var currentVersion = await db.Comments
                .AsNoTracking()
                .Where(candidate => candidate.Id == commentId)
                .Select(candidate => (int?)candidate.Version)
                .SingleOrDefaultAsync(cancellationToken);
            throw VersionConflict(expectedVersion, currentVersion);
        }

        var view = await publicContent.GetCommentForViewerAsync(comment.Id, actorId, isAdmin, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return view ?? throw ApiProblemException.NotFound("评论不存在或不可见。");
    }

    public async Task<CommentDeleteResult> DeleteAsync(
        Guid actorId,
        Guid commentId,
        int expectedVersion,
        string? reason,
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
            .SingleOrDefaultAsync(candidate => candidate.Id == commentId && candidate.DeletedAt == null, cancellationToken);
        if (comment is null)
        {
            throw ApiProblemException.NotFound("评论不存在或不可见。");
        }

        var isAdmin = actor.Role == UserRoles.Admin;
        var isOwner = comment.AuthorId == actorId;
        if (!isOwner && !isAdmin)
        {
            throw ApiProblemException.Forbidden("not_owner", "只能删除自己的评论。");
        }

        if (!isAdmin && comment.ModerationStatus == ModerationStatuses.Hidden)
        {
            throw ApiProblemException.NotFound("评论不存在或不可见。");
        }

        if (comment.Version != expectedVersion)
        {
            throw VersionConflict(expectedVersion, comment.Version);
        }

        if (isAdmin && !isOwner && string.IsNullOrWhiteSpace(reason))
        {
            throw ApiProblemException.Validation("reason", "管理员删除他人评论时必须填写原因。");
        }

        if (isAdmin && !isOwner && RuneHelper.Count(reason) > 500)
        {
            throw ApiProblemException.Validation("reason", "原因不能超过 500 个字符。");
        }

        var acceptedCleared = comment.IsAccepted;
        var post = await db.Posts
            .SingleAsync(candidate => candidate.Id == comment.PostId, cancellationToken);
        if (post.AcceptedCommentId == comment.Id)
        {
            acceptedCleared = true;
            post.AcceptedCommentId = null;
            post.IsSolved = false;
            post.Version++;
            post.UpdatedAt = now;
        }

        comment.IsAccepted = false;
        comment.ModerationStatus = ModerationStatuses.Deleted;
        comment.DeletedAt = now;
        comment.Version++;
        comment.UpdatedAt = now;

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "comment.deleted",
            "comment",
            comment.Id,
            new
            {
                postId = comment.PostId,
                version = comment.Version,
                acceptedCleared,
                deletedByAdmin = isAdmin && !isOwner,
                reason = isAdmin && !isOwner ? reason : null
            },
            now));

        if (acceptedCleared)
        {
            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                "post.acceptance_cleared",
                "post",
                post.Id,
                new { commentId = comment.Id },
                now));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var currentVersion = await db.Comments
                .AsNoTracking()
                .Where(candidate => candidate.Id == commentId)
                .Select(candidate => (int?)candidate.Version)
                .SingleOrDefaultAsync(cancellationToken);
            throw VersionConflict(expectedVersion, currentVersion);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CommentDeleteResult(comment.Id, comment.PostId, true, acceptedCleared);
    }

    private static string NormalizeBody(string? bodyMd)
    {
        var body = bodyMd ?? string.Empty;
        var count = RuneHelper.Count(body);
        if (count is < 2 or > 10_000 || string.IsNullOrWhiteSpace(body))
        {
            throw ApiProblemException.Validation("bodyMd", "评论正文长度必须为 2–10,000 个字符。");
        }

        if (body.Contains('\0')
            || body.Any(character => char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t'))
        {
            throw ApiProblemException.Validation("bodyMd", "评论正文包含无效控制字符。");
        }

        return body;
    }

    private static ApiProblemException VersionConflict(int expectedVersion, int? currentVersion)
    {
        return ApiProblemException.Conflict(
            "评论已被其他操作修改，请刷新后重试。",
            new { currentVersion, expectedVersion });
    }
}