using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record AppealView(
    Guid Id,
    string TargetType,
    Guid TargetId,
    string Reason,
    string Status,
    string? ReviewNote,
    string? TargetTitle,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record AdminAppealView(
    Guid Id,
    Guid AppellantId,
    string AppellantHandle,
    string AppellantDisplayName,
    string TargetType,
    Guid TargetId,
    string? TargetTitle,
    string Reason,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record AppealReviewCommand(string? Outcome, string? Note);

public sealed class AppealService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    EmailNotificationQueue emailQueue,
    TimeProvider timeProvider)
{
    private static readonly string[] TargetTypes =
    [
        AppealTargetTypes.Post,
        AppealTargetTypes.Comment,
        AppealTargetTypes.AccountPenalty
    ];

    public async Task<AppealView> SubmitAsync(
        Guid actorId,
        string? targetType,
        Guid targetId,
        string? reason,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var normalizedType = NormalizeTargetType(targetType);
        var normalizedReason = NormalizeReason(reason);
        string? targetTitle;
        Guid? relatedActionId = null;
        if (normalizedType == AppealTargetTypes.AccountPenalty)
        {
            if (targetId != actorId)
            {
                throw ApiProblemException.Forbidden("not_owner", "只能申诉自己的账号处罚。");
            }

            var user = await db.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == actorId, cancellationToken);
            if (user is null)
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            if (user.Status is not (UserStatuses.Muted or UserStatuses.Banned))
            {
                throw ApiProblemException.Conflict("当前没有可申诉的账号处罚。");
            }

            targetTitle = "账号处罚";
        }
        else if (normalizedType == AppealTargetTypes.Post)
        {
            var post = await db.Posts
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == targetId
                    && candidate.AuthorId == actorId
                    && candidate.DeletedAt == null, cancellationToken);
            if (post is null)
            {
                throw ApiProblemException.NotFound("帖子不存在或不属于当前用户。");
            }

            if (post.ModerationStatus != ModerationStatuses.Rejected)
            {
                throw ApiProblemException.Conflict("只有被人工驳回的帖子可以申诉。");
            }

            relatedActionId = await LatestRejectActionAsync(
                ModerationTargetTypes.Post,
                post.Id,
                cancellationToken);
            targetTitle = post.Title;
        }
        else
        {
            var comment = await db.Comments
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == targetId
                    && candidate.AuthorId == actorId
                    && candidate.DeletedAt == null, cancellationToken);
            if (comment is null)
            {
                throw ApiProblemException.NotFound("评论不存在或不属于当前用户。");
            }

            if (comment.ModerationStatus != ModerationStatuses.Rejected)
            {
                throw ApiProblemException.Conflict("只有被人工驳回的评论可以申诉。");
            }

            relatedActionId = await LatestRejectActionAsync(
                ModerationTargetTypes.Comment,
                comment.Id,
                cancellationToken);
            targetTitle = RuneHelper.Truncate(comment.BodyMd, 120);
        }

        var duplicate = await db.Appeals
            .AsNoTracking()
            .AnyAsync(appeal => appeal.AppellantId == actorId
                && appeal.TargetType == normalizedType
                && appeal.TargetId == targetId
                && appeal.Status == AppealStatuses.Pending, cancellationToken);
        if (duplicate)
        {
            throw ApiProblemException.Conflict("该目标已有待处理的申诉。");
        }

        var now = timeProvider.GetUtcNow();
        var appeal = new Appeal
        {
            AppellantId = actorId,
            TargetType = normalizedType,
            TargetId = targetId,
            RelatedActionId = relatedActionId,
            Reason = normalizedReason,
            Status = AppealStatuses.Pending,
            CreatedAt = now
        };
        db.Appeals.Add(appeal);
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "appeal.submitted",
            "appeal",
            appeal.Id,
            new { targetType = normalizedType, targetId },
            now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw ApiProblemException.Conflict("该目标已有待处理的申诉。");
        }

        return new AppealView(
            appeal.Id,
            appeal.TargetType,
            appeal.TargetId,
            appeal.Reason,
            appeal.Status,
            null,
            targetTitle,
            appeal.CreatedAt,
            null);
    }

    public async Task<IReadOnlyList<AppealView>> GetMineAsync(
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var appeals = await db.Appeals
            .AsNoTracking()
            .Where(appeal => appeal.AppellantId == actorId)
            .OrderByDescending(appeal => appeal.CreatedAt)
            .ThenByDescending(appeal => appeal.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        var titles = await LoadTargetTitlesAsync(appeals, cancellationToken);
        return appeals
            .Select(appeal => ToView(appeal, titles.GetValueOrDefault((appeal.TargetType, appeal.TargetId))))
            .ToList();
    }

    public async Task<IReadOnlyList<AdminAppealView>> GetPendingAsync(
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var appeals = await db.Appeals
            .AsNoTracking()
            .Where(appeal => appeal.Status == AppealStatuses.Pending)
            .OrderBy(appeal => appeal.CreatedAt)
            .ThenBy(appeal => appeal.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        var appellantIds = appeals.Select(appeal => appeal.AppellantId).Distinct().ToList();
        var appellants = await db.Users
            .AsNoTracking()
            .Where(user => appellantIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Handle, user.DisplayName })
            .ToDictionaryAsync(user => user.Id, cancellationToken);
        var titles = await LoadTargetTitlesAsync(appeals, cancellationToken);
        return appeals
            .Select(appeal =>
            {
                var appellant = appellants.GetValueOrDefault(appeal.AppellantId);
                return new AdminAppealView(
                    appeal.Id,
                    appeal.AppellantId,
                    appellant?.Handle ?? "unknown",
                    appellant?.DisplayName ?? "未知用户",
                    appeal.TargetType,
                    appeal.TargetId,
                    titles.GetValueOrDefault((appeal.TargetType, appeal.TargetId)),
                    appeal.Reason,
                    appeal.Status,
                    appeal.CreatedAt);
            })
            .ToList();
    }

    public async Task<AppealView> ReviewAsync(
        Guid actorId,
        Guid appealId,
        AppealReviewCommand command,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var outcome = (command.Outcome ?? string.Empty).Trim().ToLowerInvariant();
        if (outcome is not (AppealStatuses.Accepted or AppealStatuses.Rejected))
        {
            throw ApiProblemException.Validation("outcome", "outcome 必须是 accepted 或 rejected。");
        }

        var note = (command.Note ?? string.Empty).Trim();
        if (RuneHelper.Count(note) is < 1 or > 1000 || note.Contains('\0'))
        {
            throw ApiProblemException.Validation("note", "复核说明必须为 1–1000 个字符。");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var appeal = await db.Appeals
            .FromSqlInterpolated($"SELECT * FROM appeal WHERE id = {appealId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (appeal is null)
        {
            throw ApiProblemException.NotFound("申诉不存在。");
        }

        if (appeal.Status != AppealStatuses.Pending)
        {
            throw ApiProblemException.Conflict("申诉已处理。");
        }

        if (appeal.AppellantId == actorId)
        {
            throw ApiProblemException.Forbidden("self_review", "不能复核自己的申诉。");
        }

        var appellant = await db.Users
            .SingleOrDefaultAsync(user => user.Id == appeal.AppellantId, cancellationToken);
        if (appellant is null || appellant.DeletedAt is not null)
        {
            throw ApiProblemException.NotFound("申诉人不存在。");
        }

        var accepted = outcome == AppealStatuses.Accepted;
        string targetType;
        Guid targetId;
        string? targetUrl;
        var restored = false;
        if (appeal.TargetType == AppealTargetTypes.Post)
        {
            var post = await db.Posts
                .SingleOrDefaultAsync(candidate => candidate.Id == appeal.TargetId, cancellationToken);
            if (post is null || post.DeletedAt is not null)
            {
                throw ApiProblemException.NotFound("申诉目标不存在或已删除。");
            }

            if (accepted)
            {
                if (post.ModerationStatus != ModerationStatuses.Rejected)
                {
                    throw ApiProblemException.Conflict("内容状态已变化，不能通过该申诉。");
                }

                var fromStatus = post.ModerationStatus;
                post.ModerationStatus = ModerationStatuses.Approved;
                post.Version++;
                post.UpdatedAt = now;
                db.ModerationActions.Add(new ModerationAction
                {
                    TargetType = ModerationTargetTypes.Post,
                    TargetId = post.Id,
                    ActorType = ModerationActorTypes.User,
                    ActorId = actorId,
                    Action = ModerationActions.Restore,
                    FromStatus = fromStatus,
                    ToStatus = ModerationStatuses.Approved,
                    Reason = note,
                    ContentVersion = post.Version,
                    CreatedAt = now
                });
                restored = true;
            }

            targetType = "post";
            targetId = post.Id;
            targetUrl = $"/posts/{post.Id}";
        }
        else if (appeal.TargetType == AppealTargetTypes.Comment)
        {
            var comment = await db.Comments
                .SingleOrDefaultAsync(candidate => candidate.Id == appeal.TargetId, cancellationToken);
            if (comment is null || comment.DeletedAt is not null)
            {
                throw ApiProblemException.NotFound("申诉目标不存在或已删除。");
            }

            var post = await db.Posts
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == comment.PostId && candidate.DeletedAt == null, cancellationToken);
            if (post is null)
            {
                throw ApiProblemException.NotFound("申诉目标所属帖子不存在或已删除。");
            }

            if (accepted)
            {
                if (comment.ModerationStatus != ModerationStatuses.Rejected)
                {
                    throw ApiProblemException.Conflict("内容状态已变化，不能通过该申诉。");
                }

                var fromStatus = comment.ModerationStatus;
                comment.ModerationStatus = ModerationStatuses.Approved;
                comment.Version++;
                comment.UpdatedAt = now;
                db.ModerationActions.Add(new ModerationAction
                {
                    TargetType = ModerationTargetTypes.Comment,
                    TargetId = comment.Id,
                    ActorType = ModerationActorTypes.User,
                    ActorId = actorId,
                    Action = ModerationActions.Restore,
                    FromStatus = fromStatus,
                    ToStatus = ModerationStatuses.Approved,
                    Reason = note,
                    ContentVersion = comment.Version,
                    CreatedAt = now
                });
                restored = true;
            }

            targetType = "comment";
            targetId = comment.Id;
            targetUrl = $"/posts/{comment.PostId}?comment={comment.Id}";
        }
        else
        {
            if (accepted)
            {
                if (appellant.Status is not (UserStatuses.Muted or UserStatuses.Banned))
                {
                    throw ApiProblemException.Conflict("账号处罚状态已变化，不能通过该申诉。");
                }

                var previousStatus = appellant.Status;
                appellant.Status = UserStatuses.Active;
                appellant.StatusReason = null;
                appellant.StatusExpiresAt = null;
                appellant.UpdatedAt = now;
                restored = true;
                db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                    audit,
                    "user.status_changed",
                    "user",
                    appellant.Id,
                    new
                    {
                        previousStatus,
                        status = UserStatuses.Active,
                        reason = note,
                        source = "appeal"
                    },
                    now));
            }

            targetType = "user";
            targetId = appellant.Id;
            targetUrl = null;
        }

        appeal.Status = accepted ? AppealStatuses.Accepted : AppealStatuses.Rejected;
        appeal.ReviewedBy = actorId;
        appeal.ReviewNote = note;
        appeal.ResolvedAt = now;

        var summary = accepted
            ? restored
                ? appeal.TargetType == AppealTargetTypes.AccountPenalty
                    ? "你的申诉已通过复核，账号处罚已解除。"
                    : "你的申诉已通过复核，内容已恢复。"
                : "你的申诉已通过复核。"
            : "你的申诉未通过复核。";
        var notificationPayload = new
        {
            summary,
            targetType,
            targetId,
            targetUrl,
            action = accepted ? "accepted" : "rejected"
        };
        var dedupeKey = $"appeal-result:{appeal.Id:N}";
        db.Notifications.Add(NotificationWriter.Create(
            appellant.Id,
            NotificationTypes.AppealResult,
            notificationPayload,
            dedupeKey,
            now));
        await emailQueue.EnqueueForNotificationAsync(
            appellant.Id,
            NotificationTypes.AppealResult,
            notificationPayload,
            dedupeKey,
            now,
            cancellationToken);

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            accepted ? "appeal.accepted" : "appeal.rejected",
            "appeal",
            appeal.Id,
            new
            {
                targetType = appeal.TargetType,
                targetId = appeal.TargetId,
                outcome = appeal.Status,
                restored,
                reviewNote = note
            },
            now));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var titles = await LoadTargetTitlesAsync([appeal], cancellationToken);
        return ToView(appeal, titles.GetValueOrDefault((appeal.TargetType, appeal.TargetId)));
    }

    private async Task<Guid?> LatestRejectActionAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        return await db.ModerationActions
            .AsNoTracking()
            .Where(action => action.TargetType == targetType
                && action.TargetId == targetId
                && action.Action == ModerationActions.Reject)
            .OrderByDescending(action => action.CreatedAt)
            .ThenByDescending(action => action.Id)
            .Select(action => (Guid?)action.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Dictionary<(string, Guid), string>> LoadTargetTitlesAsync(
        IReadOnlyList<Appeal> appeals,
        CancellationToken cancellationToken)
    {
        var postIds = appeals
            .Where(appeal => appeal.TargetType == AppealTargetTypes.Post)
            .Select(appeal => appeal.TargetId)
            .Distinct()
            .ToList();
        var commentIds = appeals
            .Where(appeal => appeal.TargetType == AppealTargetTypes.Comment)
            .Select(appeal => appeal.TargetId)
            .Distinct()
            .ToList();
        var posts = await db.Posts
            .AsNoTracking()
            .Where(post => postIds.Contains(post.Id))
            .Select(post => new { post.Id, post.Title })
            .ToDictionaryAsync(post => post.Id, post => post.Title, cancellationToken);
        var comments = await db.Comments
            .AsNoTracking()
            .Where(comment => commentIds.Contains(comment.Id))
            .Select(comment => new { comment.Id, comment.BodyMd })
            .ToDictionaryAsync(
                comment => comment.Id,
                comment => RuneHelper.Truncate(comment.BodyMd, 120),
                cancellationToken);
        var result = new Dictionary<(string, Guid), string>();
        foreach (var post in posts)
        {
            result[(AppealTargetTypes.Post, post.Key)] = post.Value;
        }

        foreach (var comment in comments)
        {
            result[(AppealTargetTypes.Comment, comment.Key)] = comment.Value;
        }

        foreach (var appeal in appeals.Where(candidate =>
                     candidate.TargetType == AppealTargetTypes.AccountPenalty))
        {
            result[(AppealTargetTypes.AccountPenalty, appeal.TargetId)] = "账号处罚";
        }

        return result;
    }

    private static AppealView ToView(Appeal appeal, string? targetTitle)
    {
        return new AppealView(
            appeal.Id,
            appeal.TargetType,
            appeal.TargetId,
            appeal.Reason,
            appeal.Status,
            appeal.ReviewNote,
            targetTitle,
            appeal.CreatedAt,
            appeal.ResolvedAt);
    }

    private static string NormalizeTargetType(string? targetType)
    {
        var normalized = (targetType ?? string.Empty).Trim().ToLowerInvariant();
        if (!TargetTypes.Contains(normalized, StringComparer.Ordinal))
        {
            throw ApiProblemException.Validation(
                "targetType",
                "targetType 必须是 post、comment 或 account_penalty。");
        }

        return normalized;
    }

    private static string NormalizeReason(string? reason)
    {
        var normalized = (reason ?? string.Empty).Trim();
        if (RuneHelper.Count(normalized) is < 1 or > 2000
            || normalized.Contains('\0'))
        {
            throw ApiProblemException.Validation("reason", "申诉理由必须为 1–2000 个字符。");
        }

        return normalized;
    }
}