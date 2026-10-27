using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Interactions;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record ReportCreateCommand(
    string? TargetType,
    Guid TargetId,
    string? Reason,
    string? Detail);

public sealed record ReportReceiptView(
    Guid Id,
    string TargetType,
    Guid TargetId,
    string Reason,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record ReporterSummaryView(string Handle);

public sealed record AdminReportView(
    Guid Id,
    ReporterSummaryView Reporter,
    string TargetType,
    Guid TargetId,
    string? TargetPreview,
    string Reason,
    string? Detail,
    string Status,
    DateTimeOffset? HandledAt,
    string? ResolutionNote,
    DateTimeOffset CreatedAt);

public sealed record AdminReportPage(
    IReadOnlyList<AdminReportView> Items,
    string? NextCursor,
    bool InvalidCursor);

public sealed record ReportResolveActionInput(
    string? Type,
    string? Reason,
    DateTimeOffset? ExpiresAt);

public sealed record ReportResolveCommand(
    string? Outcome,
    string? ResolutionNote,
    IReadOnlyList<ReportResolveActionInput>? Actions);

public sealed class ReportService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    ContentModerationService moderation,
    AdminUserService adminUsers,
    EmailNotificationQueue emailQueue,
    TimeProvider timeProvider)
{
    private static readonly string[] AllowedReasons =
    [
        ReportReasons.Spam,
        ReportReasons.Harassment,
        ReportReasons.Misclassified,
        ReportReasons.Plagiarism,
        ReportReasons.Other
    ];

    public async Task<ReportReceiptView> CreateAsync(
        Guid actorId,
        ReportCreateCommand command,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireWriteAccessAsync(actorId, ActorWriteScope.Report, cancellationToken);
        var targetType = NormalizeTargetType(command.TargetType);
        if (command.TargetId == Guid.Empty)
        {
            throw ApiProblemException.Validation("targetId", "举报目标不能为空。");
        }

        var reason = (command.Reason ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedReasons.Contains(reason, StringComparer.Ordinal))
        {
            throw ApiProblemException.Validation("reason", "举报原因无效。");
        }

        var detail = string.IsNullOrWhiteSpace(command.Detail) ? null : command.Detail.Trim();
        if (detail is not null
            && (RuneHelper.Count(detail) > 1000 || detail.Contains('\0')))
        {
            throw ApiProblemException.Validation("detail", "补充说明不能超过 1000 个字符。");
        }

        await EnsureTargetVisibleAsync(targetType, command.TargetId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var report = new Report
        {
            ReporterId = actorId,
            TargetType = targetType,
            TargetId = command.TargetId,
            Reason = reason,
            Detail = detail,
            Status = ReportStatuses.Pending,
            CreatedAt = now
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Reports.Add(report);
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "report.created",
            targetType,
            command.TargetId,
            new { reason },
            now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ReportReceiptView(
            report.Id,
            report.TargetType,
            report.TargetId,
            report.Reason,
            report.Status,
            report.CreatedAt);
    }

    public async Task<AdminReportPage> ListAsync(
        Guid actorId,
        string? status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var normalizedStatus = string.IsNullOrWhiteSpace(status)
            ? ReportStatuses.Pending
            : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not (ReportStatuses.Pending
            or ReportStatuses.Resolved
            or ReportStatuses.Dismissed))
        {
            throw ApiProblemException.Validation("status", "举报状态筛选无效。");
        }

        var scope = AdminCursor.BuildScope("admin-reports", normalizedStatus);
        DateTimeOffset? cursorTime = null;
        Guid cursorId = default;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!AdminCursor.TryDecode(cursor, scope, out var decodedTime, out var decodedId))
            {
                return new AdminReportPage([], null, true);
            }

            cursorTime = decodedTime;
            cursorId = decodedId;
        }

        var query = db.Reports
            .AsNoTracking()
            .Where(report => report.Status == normalizedStatus);
        if (cursorTime is not null)
        {
            query = query.Where(report =>
                report.CreatedAt > cursorTime
                || (report.CreatedAt == cursorTime && report.Id.CompareTo(cursorId) > 0));
        }

        var rows = await query
            .OrderBy(report => report.CreatedAt)
            .ThenBy(report => report.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        var pageRows = hasMore ? rows.Take(limit).ToList() : rows;
        var items = await BuildViewsAsync(pageRows, cancellationToken);
        var nextCursor = hasMore
            ? AdminCursor.Encode(scope, pageRows[^1].CreatedAt, pageRows[^1].Id)
            : null;
        return new AdminReportPage(items, nextCursor, false);
    }

    public async Task<AdminReportView> ResolveAsync(
        Guid actorId,
        Guid reportId,
        ReportResolveCommand command,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var outcome = (command.Outcome ?? string.Empty).Trim().ToLowerInvariant();
        if (outcome is not (ReportStatuses.Resolved or ReportStatuses.Dismissed))
        {
            throw ApiProblemException.Validation("outcome", "outcome 必须是 resolved 或 dismissed。");
        }

        var note = (command.ResolutionNote ?? string.Empty).Trim();
        if (RuneHelper.Count(note) is < 1 or > 500 || note.Contains('\0'))
        {
            throw ApiProblemException.Validation("resolutionNote", "处理备注必须为 1–500 个字符。");
        }

        var actions = command.Actions ?? [];
        if (actions.Count > 4)
        {
            throw ApiProblemException.Validation("actions", "一次最多执行 4 个处理动作。");
        }

        if (outcome == ReportStatuses.Dismissed && actions.Count != 0)
        {
            throw ApiProblemException.Validation("actions", "驳回举报时不能执行处罚或内容动作。");
        }

        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var report = await db.Reports
                .FromSqlInterpolated($"SELECT * FROM \"report\" WHERE id = {reportId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (report is null)
            {
                throw ApiProblemException.NotFound("举报不存在。");
            }

            if (report.Status != ReportStatuses.Pending)
            {
                throw ApiProblemException.Conflict("该举报已处理，不能重复操作。");
            }

            var normalizedActions = actions
                .Select((action, index) => NormalizeAction(action, index))
                .ToList();
            foreach (var action in normalizedActions)
            {
                await ApplyActionAsync(actorId, report, action, audit, cancellationToken);
            }

            var now = timeProvider.GetUtcNow();
            report.Status = outcome;
            report.HandledBy = actorId;
            report.HandledAt = now;
            report.ResolutionNote = note;

            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                outcome == ReportStatuses.Resolved ? "report.resolved" : "report.dismissed",
                "report",
                report.Id,
                new
                {
                    outcome,
                    resolutionNote = note,
                    actions = normalizedActions.Select(action => new { type = action.Type, reason = action.Reason })
                },
                now));

            var reporterPayload = new
            {
                summary = "你的举报已有处理结果。",
                reportId = report.Id,
                targetType = report.TargetType,
                targetId = report.TargetId,
                status = outcome,
                resolutionNote = note
            };
            var reporterDedupe = $"report-result:reporter:{report.Id}";
            db.Notifications.Add(NotificationWriter.Create(
                report.ReporterId,
                NotificationTypes.ReportResult,
                reporterPayload,
                reporterDedupe,
                now));
            await emailQueue.EnqueueForNotificationAsync(
                report.ReporterId,
                NotificationTypes.ReportResult,
                reporterPayload,
                reporterDedupe,
                now,
                cancellationToken);

            var targetAuthorId = report.TargetType == ReportTargetTypes.User
                ? report.TargetId
                : await moderation.GetContentAuthorIdAsync(
                    report.TargetType,
                    report.TargetId,
                    cancellationToken);
            if (targetAuthorId is not null && targetAuthorId != report.ReporterId)
            {
                var targetPayload = new
                {
                    summary = "与你的内容相关的举报已有处理结果。",
                    reportId = report.Id,
                    targetType = report.TargetType,
                    targetId = report.TargetId,
                    status = outcome
                };
                var targetDedupe = $"report-result:target:{report.Id}";
                db.Notifications.Add(NotificationWriter.Create(
                    targetAuthorId.Value,
                    NotificationTypes.ReportResult,
                    targetPayload,
                    targetDedupe,
                    now));
                await emailQueue.EnqueueForNotificationAsync(
                    targetAuthorId.Value,
                    NotificationTypes.ReportResult,
                    targetPayload,
                    targetDedupe,
                    now,
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            var view = (await BuildViewsAsync([report], cancellationToken)).Single();
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return view;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task ApplyActionAsync(
        Guid actorId,
        Report report,
        NormalizedResolveAction action,
        AuditContext audit,
        CancellationToken cancellationToken)
    {
        if (action.Type is "content_hide" or "content_delete")
        {
            if (report.TargetType is not (ReportTargetTypes.Post or ReportTargetTypes.Comment))
            {
                throw ApiProblemException.Validation("actions", "只有内容举报可以执行内容动作。");
            }

            await moderation.ApplyForReportAsync(
                actorId,
                report.TargetType,
                report.TargetId,
                action.Type == "content_hide" ? ModerationActions.Hide : ModerationActions.Delete,
                action.Reason,
                audit,
                cancellationToken);
            return;
        }

        Guid? targetUserId = report.TargetType == ReportTargetTypes.User
            ? report.TargetId
            : await moderation.GetContentAuthorIdAsync(report.TargetType, report.TargetId, cancellationToken);
        if (targetUserId is null)
        {
            throw ApiProblemException.NotFound("举报目标作者不存在。");
        }

        await adminUsers.ChangeStatusAsync(
            actorId,
            targetUserId.Value,
            action.Type == "user_mute" ? UserStatuses.Muted : UserStatuses.Banned,
            action.Reason,
            action.ExpiresAt,
            audit,
            cancellationToken);
    }

    private async Task EnsureTargetVisibleAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var visible = targetType switch
        {
            ReportTargetTypes.Post => await db.Posts
                .AsNoTracking()
                .AnyAsync(post => post.Id == targetId
                    && post.ModerationStatus == ModerationStatuses.Approved
                    && post.DeletedAt == null, cancellationToken),
            ReportTargetTypes.Comment => await db.Comments
                .AsNoTracking()
                .Where(comment => comment.Id == targetId
                    && comment.ModerationStatus == ModerationStatuses.Approved
                    && comment.DeletedAt == null)
                .Join(
                    db.Posts.AsNoTracking(),
                    comment => comment.PostId,
                    post => post.Id,
                    (comment, post) => post)
                .AnyAsync(post => post.ModerationStatus == ModerationStatuses.Approved
                    && post.DeletedAt == null, cancellationToken),
            ReportTargetTypes.User => await db.Users
                .AsNoTracking()
                .AnyAsync(user => user.Id == targetId
                    && (user.Status == UserStatuses.Active || user.Status == UserStatuses.Muted)
                    && user.ActivatedAt != null
                    && user.DeletedAt == null, cancellationToken),
            _ => false
        };

        if (!visible)
        {
            throw ApiProblemException.NotFound("举报目标不存在或不可见。");
        }
    }

    private async Task<IReadOnlyList<AdminReportView>> BuildViewsAsync(
        IReadOnlyList<Report> reports,
        CancellationToken cancellationToken)
    {
        if (reports.Count == 0)
        {
            return [];
        }

        var reporterIds = reports.Select(report => report.ReporterId).Distinct().ToList();
        var reporters = await db.Users
            .AsNoTracking()
            .Where(user => reporterIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Handle })
            .ToDictionaryAsync(user => user.Id, user => user.Handle, cancellationToken);

        var postIds = reports
            .Where(report => report.TargetType == ReportTargetTypes.Post)
            .Select(report => report.TargetId)
            .Distinct()
            .ToList();
        var commentIds = reports
            .Where(report => report.TargetType == ReportTargetTypes.Comment)
            .Select(report => report.TargetId)
            .Distinct()
            .ToList();
        var userIds = reports
            .Where(report => report.TargetType == ReportTargetTypes.User)
            .Select(report => report.TargetId)
            .Distinct()
            .ToList();

        var posts = await db.Posts
            .AsNoTracking()
            .Where(post => postIds.Contains(post.Id))
            .Select(post => new { post.Id, post.Title, post.Excerpt })
            .ToDictionaryAsync(post => post.Id, post => $"{post.Title} {post.Excerpt}", cancellationToken);
        var comments = await db.Comments
            .AsNoTracking()
            .Where(comment => commentIds.Contains(comment.Id))
            .Select(comment => new { comment.Id, comment.BodyMd })
            .ToDictionaryAsync(comment => comment.Id, comment => comment.BodyMd, cancellationToken);
        var users = await db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Handle, user.DisplayName })
            .ToDictionaryAsync(user => user.Id, user => $"@{user.Handle} {user.DisplayName}", cancellationToken);

        return reports.Select(report => new AdminReportView(
                report.Id,
                new ReporterSummaryView(reporters.GetValueOrDefault(report.ReporterId, "unknown")),
                report.TargetType,
                report.TargetId,
                Preview(report, posts, comments, users),
                report.Reason,
                report.Detail,
                report.Status,
                report.HandledAt,
                report.ResolutionNote,
                report.CreatedAt))
            .ToList();
    }

    private static string? Preview(
        Report report,
        IReadOnlyDictionary<Guid, string> posts,
        IReadOnlyDictionary<Guid, string> comments,
        IReadOnlyDictionary<Guid, string> users)
    {
        var value = report.TargetType switch
        {
            ReportTargetTypes.Post => posts.GetValueOrDefault(report.TargetId),
            ReportTargetTypes.Comment => comments.GetValueOrDefault(report.TargetId),
            ReportTargetTypes.User => users.GetValueOrDefault(report.TargetId),
            _ => null
        };
        return value is null ? null : RuneHelper.Truncate(value, 500);
    }

    private static string NormalizeTargetType(string? targetType)
    {
        var normalized = (targetType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not (ReportTargetTypes.Post or ReportTargetTypes.Comment or ReportTargetTypes.User))
        {
            throw ApiProblemException.Validation("targetType", "targetType 必须是 post、comment 或 user。");
        }

        return normalized;
    }

    private static NormalizedResolveAction NormalizeAction(ReportResolveActionInput? action, int index)
    {
        if (action is null)
        {
            throw ApiProblemException.Validation($"actions[{index}]", "处理动作不能为空。");
        }

        var type = (action.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type is not ("content_hide" or "content_delete" or "user_mute" or "user_ban"))
        {
            throw ApiProblemException.Validation($"actions[{index}].type", "举报处理动作无效。");
        }

        var reason = (action.Reason ?? string.Empty).Trim();
        if (RuneHelper.Count(reason) is < 1 or > 500)
        {
            throw ApiProblemException.Validation($"actions[{index}].reason", "处理动作原因必须为 1–500 个字符。");
        }

        return new NormalizedResolveAction(
            type,
            reason,
            action.ExpiresAt?.ToUniversalTime());
    }

    private sealed record NormalizedResolveAction(
        string Type,
        string Reason,
        DateTimeOffset? ExpiresAt);
}