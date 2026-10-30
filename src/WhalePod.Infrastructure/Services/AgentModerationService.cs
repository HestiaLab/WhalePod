using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Npgsql;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed partial class AgentModerationService
{
    private readonly WhalePodDbContext db;
    private readonly AiSettingsService aiSettings;
    private readonly AiBudgetService aiBudget;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentModerationService> logger;
    private readonly AgentTaskSynchronizer synchronizer;
    private readonly AgentContentProjector projector;
    private readonly AgentTaskLeaseService leaseService;
    private readonly AgentBudgetGate budgetGate;

    public AgentModerationService(
        WhalePodDbContext db,
        AiSettingsService aiSettings,
        AiBudgetService aiBudget,
        TimeProvider timeProvider,
        ILogger<AgentModerationService> logger)
    {
        this.db = db;
        this.aiSettings = aiSettings;
        this.aiBudget = aiBudget;
        this.timeProvider = timeProvider;
        this.logger = logger;
        synchronizer = new AgentTaskSynchronizer(db, logger);
        projector = new AgentContentProjector(db);
        leaseService = new AgentTaskLeaseService(db);
        budgetGate = new AgentBudgetGate(aiSettings, aiBudget, logger);
    }

    internal static readonly string[] ActiveTaskStatuses =
    [
        ModerationTaskStatuses.Queued,
        ModerationTaskStatuses.Claimed,
        ModerationTaskStatuses.Suggested,
        ModerationTaskStatuses.Escalated
    ];

    public async Task<AgentTaskListPage> ListTasksAsync(
        AgentIdentity identity,
        string? instanceHeader,
        string? status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var settings = await budgetGate.RequireEnabledAsync(cancellationToken);
        var instance = await RequireInstanceAsync(identity, instanceHeader, cancellationToken);
        var normalizedStatus = NormalizeTaskFilter(status);
        var normalizedLimit = Math.Clamp(limit, 1, 50);
        var now = timeProvider.GetUtcNow();
        var budget = await aiBudget.GetAsync(settings, cancellationToken);
        if (!settings.SuggestionsPaused && !budget.ContentBudgetExceeded)
        {
            await synchronizer.SynchronizeAsync(now, budget.DailyContentBudget, cancellationToken);
        }

        var expired = await leaseService.ReleaseExpiredAsync(now, cancellationToken);
        if (expired > 0)
        {
            await RecordLeaseExpiryAsync(expired, now, cancellationToken);
        }

        var scope = $"agent-tasks|{instance.Id:N}|{normalizedStatus}";
        var query = db.ModerationTasks
            .AsNoTracking()
            .Where(task => normalizedStatus == "queued"
                ? task.Status == ModerationTaskStatuses.Queued
                : task.Status == ModerationTaskStatuses.Claimed
                    && task.ClaimedByAgentInstanceId == instance.Id);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, scope, out var cursorPriority, out var cursorCreatedAt, out var cursorId))
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效。");
            }

            query = query.Where(task =>
                task.Priority < cursorPriority
                || (task.Priority == cursorPriority
                    && (task.CreatedAt > cursorCreatedAt
                        || (task.CreatedAt == cursorCreatedAt && task.Id.CompareTo(cursorId) > 0))));
        }

        var rows = await query
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.CreatedAt)
            .ThenBy(task => task.Id)
            .Take(normalizedLimit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > normalizedLimit;
        var pageRows = hasMore ? rows.Take(normalizedLimit).ToList() : rows;
        var items = new List<AgentTaskSummaryView>();
        foreach (var task in pageRows)
        {
            if (!await EnsureTaskFreshAsync(task, now, cancellationToken))
            {
                continue;
            }

            items.Add(AgentContentProjector.ToSummary(task));
        }

        var nextCursor = hasMore && pageRows.Count > 0
            ? EncodeCursor(scope, pageRows[^1].Priority, pageRows[^1].CreatedAt, pageRows[^1].Id)
            : null;
        return new AgentTaskListPage(items, nextCursor);
    }

    public async Task<AgentTaskDetailView> GetTaskAsync(
        AgentIdentity identity,
        string? instanceHeader,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await budgetGate.RequireEnabledAsync(cancellationToken);
        var instance = await RequireInstanceAsync(identity, instanceHeader, cancellationToken);
        var task = await db.ModerationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null
            || (task.Status != ModerationTaskStatuses.Queued
                && !(task.Status == ModerationTaskStatuses.Claimed
                    && task.ClaimedByAgentInstanceId == instance.Id)))
        {
            throw TaskNotFound();
        }

        var now = timeProvider.GetUtcNow();
        if (task.Status == ModerationTaskStatuses.Claimed
            && task.LeaseExpiresAt is not null
            && task.LeaseExpiresAt <= now)
        {
            var released = await leaseService.ReleaseLeaseAsync(task.Id, now, "lease_expired", cancellationToken);
            if (released > 0)
            {
                await RecordLeaseExpiryAsync(released, now, cancellationToken);
            }
        }

        if (!await EnsureTaskFreshAsync(task, now, cancellationToken))
        {
            throw TaskNotFound();
        }

        var detail = await projector.BuildTaskDetailAsync(task.Id, cancellationToken);
        return detail ?? throw TaskNotFound();
    }

    public async Task<AgentClaimResultView> ClaimTaskAsync(
        AgentIdentity identity,
        string? instanceHeader,
        Guid taskId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var settings = await budgetGate.RequireEnabledAsync(cancellationToken);
        if (settings.SuggestionsPaused)
        {
            throw AgentDisabled();
        }

        var instance = await RequireInstanceAsync(identity, instanceHeader, cancellationToken);
        await budgetGate.EnsureNewWorkAllowedAsync(settings, identity, audit, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var expired = await leaseService.ReleaseExpiredAsync(now, cancellationToken);
        if (expired > 0)
        {
            await RecordLeaseExpiryAsync(expired, now, cancellationToken);
        }
        var task = await db.ModerationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null)
        {
            throw TaskNotFound();
        }

        if (!await EnsureTaskFreshAsync(task, now, cancellationToken))
        {
            throw new ApiProblemException(
                ApiErrorCodes.ContentVersionStale,
                409,
                "内容版本已变化，请重新领取任务。",
                new { currentVersion = task.ContentVersion });
        }

        if (task.Status == ModerationTaskStatuses.Claimed
            && task.LeaseExpiresAt is not null
            && task.LeaseExpiresAt > now
            && task.ClaimedByAgentInstanceId != instance.Id)
        {
            throw new ApiProblemException(
                ApiErrorCodes.TaskAlreadyClaimed,
                409,
                "任务已被其他 Agent 领取，请领取其他任务。");
        }

        if (task.Status is ModerationTaskStatuses.Suggested
            or ModerationTaskStatuses.HumanDecided
            or ModerationTaskStatuses.Escalated
            or ModerationTaskStatuses.Expired)
        {
            throw new ApiProblemException(
                ApiErrorCodes.TaskAlreadyDecided,
                409,
                "任务已结束，不能再次领取。");
        }

        var lease = await leaseService.TryClaimAsync(taskId, instance.Id, now, cancellationToken);
        if (lease is null)
        {
            throw new ApiProblemException(
                ApiErrorCodes.TaskAlreadyClaimed,
                409,
                "任务已被其他 Agent 领取，请领取其他任务。");
        }

        db.AuditLogs.Add(AuditLogWriter.CreateServiceAction(
            audit,
            "agent.task_claimed",
            "moderation_task",
            taskId,
            new
            {
                instanceId = instance.Id,
                targetType = task.TargetType,
                targetId = task.TargetId,
                contentVersion = task.ContentVersion,
                leaseExpiresAt = lease.ExpiresAt
            },
            now));
        await db.SaveChangesAsync(cancellationToken);
        var detail = await projector.BuildTaskDetailAsync(taskId, cancellationToken)
            ?? throw TaskNotFound();
        return new AgentClaimResultView(detail, lease.ExpiresAt);
    }

    public async Task<AgentReleaseResultView> ReleaseTaskAsync(
        AgentIdentity identity,
        string? instanceHeader,
        Guid taskId,
        AgentReleaseRequest? request,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await budgetGate.RequireEnabledAsync(cancellationToken);
        var instance = await RequireInstanceAsync(identity, instanceHeader, cancellationToken);
        var reason = NormalizeText(request?.Reason, 200, "reason", required: false);
        var task = await db.ModerationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null)
        {
            throw TaskNotFound();
        }

        if (task.Status == ModerationTaskStatuses.Queued && task.ClaimedByAgentInstanceId is null)
        {
            return new AgentReleaseResultView(true, task.Id, ModerationTaskStatuses.Queued);
        }

        if (task.Status != ModerationTaskStatuses.Claimed
            || task.ClaimedByAgentInstanceId != instance.Id)
        {
            throw new ApiProblemException(
                ApiErrorCodes.TaskLeaseExpired,
                409,
                "任务租约已过期或不属于当前实例。");
        }

        var now = timeProvider.GetUtcNow();
        var released = await leaseService.TryReleaseAsync(
            taskId,
            instance.Id,
            task.LeaseToken,
            "released",
            cancellationToken);
        if (!released)
        {
            throw new ApiProblemException(
                ApiErrorCodes.TaskLeaseExpired,
                409,
                "任务租约已过期或不属于当前实例。");
        }

        db.AuditLogs.Add(AuditLogWriter.CreateServiceAction(
            audit,
            "agent.task_released",
            "moderation_task",
            task.Id,
            new { instanceId = instance.Id, reason },
            now));
        await db.SaveChangesAsync(cancellationToken);
        return new AgentReleaseResultView(true, task.Id, ModerationTaskStatuses.Queued);
    }

    private async Task<bool> EnsureTaskFreshAsync(
        ModerationTask task,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var snapshot = await projector.GetCurrentSnapshotAsync(task, cancellationToken);
        if (snapshot is null)
        {
            await leaseService.ExpireAsync(task.Id, "content_missing", now, cancellationToken);
            return false;
        }

        var hash = snapshot.Post is not null
            ? AgentContentHash.ForPost(
                snapshot.Post.Id,
                snapshot.Post.Version,
                snapshot.Post.Title,
                snapshot.Post.BodyMd,
                snapshot.Post.CategoryId)
            : AgentContentHash.ForComment(
                snapshot.Comment!.Id,
                snapshot.Comment.Version,
                snapshot.Comment.PostId,
                snapshot.Comment.ParentId,
                snapshot.Comment.BodyMd);
        if (task.ContentVersion != snapshot.Version || task.ContentHash != hash)
        {
            await leaseService.ExpireAsync(task.Id, "content_stale", now, cancellationToken);
            return false;
        }

        return true;
    }

    private async Task RecordLeaseExpiryAsync(
        int count,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.AuditLogs.Add(AuditLogWriter.CreateSystemAction(
            AiBudgetService.LeaseExpiredAction,
            "agent_task_lease",
            null,
            new
            {
                count,
                releasedAt = now
            },
            now));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeTaskFilter(string? status)
    {
        var normalized = string.IsNullOrWhiteSpace(status)
            ? "queued"
            : status.Trim().ToLowerInvariant();
        if (normalized is not ("queued" or "claimed"))
        {
            throw ApiProblemException.Validation("status", "status 必须是 queued 或 claimed。");
        }

        return normalized;
    }

    private static string NormalizeText(
        string? value,
        int maxLength,
        string field,
        bool required)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            if (required)
            {
                throw SuggestionOrValidationFailure(field, "字段不能为空。");
            }

            return string.Empty;
        }

        if (RuneHelper.Count(normalized) > maxLength
            || normalized.Any(char.IsControl))
        {
            throw SuggestionOrValidationFailure(field, $"字段长度不能超过 {maxLength} 且不能包含控制字符。");
        }

        return normalized;
    }

    internal static ApiProblemException AgentDisabled()
    {
        return new ApiProblemException(
            ApiErrorCodes.AgentDisabled,
            503,
            "AI/MCP 已关闭，Agent 接口不可用。");
    }

    private static ApiProblemException TaskNotFound()
    {
        return new ApiProblemException(
            ApiErrorCodes.TaskNotFound,
            404,
            "任务不存在或当前不可见。");
    }

    private static ApiProblemException SuggestionOrValidationFailure(string field, string message)
    {
        return SuggestionFailure(field, message);
    }

    private static ApiProblemException SuggestionFailure(string field, string message)
    {
        return new ApiProblemException(
            ApiErrorCodes.SuggestionInvalid,
            400,
            "审核建议字段校验失败。",
            new { issues = new[] { new { field, message } } });
    }

    private static string EncodeCursor(
        string scope,
        int priority,
        DateTimeOffset createdAt,
        Guid id)
    {
        var json = JsonSerializer.Serialize(new
        {
            v = 2,
            s = scope,
            p = priority,
            c = createdAt.UtcTicks,
            id
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeCursor(
        string cursor,
        string scope,
        out int priority,
        out DateTimeOffset createdAt,
        out Guid id)
    {
        priority = 0;
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 512)
        {
            return false;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var json = Convert.FromBase64String(normalized);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("s").GetString() != scope
                || !root.TryGetProperty("v", out var versionElement)
                || !versionElement.TryGetInt32(out var version)
                || version != 2
                || !root.TryGetProperty("p", out var priorityElement)
                || !root.TryGetProperty("c", out var createdElement)
                || !root.TryGetProperty("id", out var idElement)
                || !priorityElement.TryGetInt32(out priority)
                || !createdElement.TryGetInt64(out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks
                || ticks > DateTimeOffset.MaxValue.UtcTicks
                || !Guid.TryParse(idElement.GetString(), out id))
            {
                return false;
            }

            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record AgentSuggestionInput(
        string Suggestion,
        string PolicyCode,
        string Reason,
        decimal Confidence,
        int ContentVersion,
        string PolicyVersion,
        AgentModelInfo Model,
        string PromptVersion,
        string IdempotencyKey);

    private static class SuggestionTypes
    {
        public static readonly string[] All = AgentPolicyDefaults.SuggestionTypes;
    }
}