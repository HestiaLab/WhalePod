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

public sealed class AgentModerationService(
    WhalePodDbContext db,
    AiSettingsService aiSettings,
    AiBudgetService aiBudget,
    TimeProvider timeProvider,
    ILogger<AgentModerationService> logger)
{
    private static readonly string[] ActiveTaskStatuses =
    [
        ModerationTaskStatuses.Queued,
        ModerationTaskStatuses.Claimed,
        ModerationTaskStatuses.Suggested,
        ModerationTaskStatuses.Escalated
    ];

    public async Task<AgentPolicyView> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await RequireEnabledAsync(cancellationToken);
        return ToPolicy(settings);
    }

    public async Task<AgentHeartbeatResultView> HeartbeatAsync(
        AgentIdentity identity,
        AgentHeartbeatRequest request,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireEnabledAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var instanceName = NormalizeText(request.InstanceName, 80, "instanceName", required: true);
        var hostFingerprint = NormalizeText(request.HostFingerprint, 64, "hostFingerprint", required: true);
        if (!Regex.IsMatch(hostFingerprint, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
        {
            throw SuggestionOrValidationFailure("hostFingerprint", "hostFingerprint 必须是 64 位小写十六进制。");
        }

        var agentVersion = NormalizeText(request.AgentVersion, 40, "agentVersion", required: true);
        var mcpVersion = NormalizeText(request.McpVersion, 40, "mcpVersion", required: false);
        var runtimeLocation = NormalizeText(request.RuntimeLocation, 20, "runtimeLocation", required: true);
        var model = NormalizeModel(request.Model, "model");
        if (request.QueueDepth < 0)
        {
            throw SuggestionOrValidationFailure("queueDepth", "queueDepth 不能为负数。");
        }

        var instance = await db.AgentInstances
            .SingleOrDefaultAsync(
                candidate => candidate.ServiceAccountId == identity.ServiceAccountId
                    && candidate.InstanceName == instanceName,
                cancellationToken);

        if (instance?.Status == AgentInstanceStatuses.Revoked)
        {
            throw ApiProblemException.Forbidden("instance_revoked", "Agent 实例已被撤销。");
        }

        var modelApproved = runtimeLocation == RuntimeLocations.Local
            && model.Runtime == RuntimeLocations.Local
            && IsModelApproved(settings, model);
        if (!modelApproved)
        {
            if (instance is not null)
            {
                instance.Status = AgentInstanceStatuses.Paused;
                instance.StatusReason = "model_or_runtime_unapproved";
                instance.LastHeartbeatAt = now;
                db.AuditLogs.Add(AuditLogWriter.CreateServiceAction(
                    audit,
                    "agent.instance_paused",
                    "agent_instance",
                    instance.Id,
                    new
                    {
                        reason = "model_or_runtime_unapproved",
                        modelName = model.Name,
                        modelVersion = model.Version,
                        modelQuantization = model.Quantization,
                        runtimeLocation
                    },
                    now));
                await db.SaveChangesAsync(cancellationToken);
            }

            throw new ApiProblemException(
                ApiErrorCodes.Forbidden,
                403,
                "模型或运行位置未获批准，实例已暂停。",
                new { reason = "model_or_runtime_unapproved" });
        }

        var created = instance is null;
        if (instance is null)
        {
            instance = new AgentInstance
            {
                ServiceAccountId = identity.ServiceAccountId,
                InstanceName = instanceName,
                HostFingerprint = hostFingerprint,
                RuntimeLocation = RuntimeLocations.Local,
                AgentVersion = agentVersion,
                McpVersion = mcpVersion,
                ModelName = model.Name!,
                ModelVersion = model.Version!,
                ModelQuantization = model.Quantization!,
                ModelRuntime = RuntimeLocations.Local,
                Status = AgentInstanceStatuses.Active,
                CreatedAt = now
            };
            db.AgentInstances.Add(instance);
        }
        else if (instance.Status == AgentInstanceStatuses.Paused)
        {
            instance.Status = AgentInstanceStatuses.Active;
            instance.StatusReason = null;
            db.AuditLogs.Add(AuditLogWriter.CreateServiceAction(
                audit,
                "agent.instance_resumed",
                "agent_instance",
                instance.Id,
                new { modelName = model.Name },
                now));
        }

        instance.HostFingerprint = hostFingerprint;
        instance.AgentVersion = agentVersion;
        instance.McpVersion = mcpVersion;
        instance.RuntimeLocation = RuntimeLocations.Local;
        instance.ModelName = model.Name!;
        instance.ModelVersion = model.Version!;
        instance.ModelQuantization = model.Quantization!;
        instance.ModelRuntime = RuntimeLocations.Local;
        instance.QueueDepth = request.QueueDepth;
        instance.LastHeartbeatAt = now;
        if (created)
        {
            db.AuditLogs.Add(AuditLogWriter.CreateServiceAction(
                audit,
                "agent.instance_registered",
                "agent_instance",
                instance.Id,
                new
                {
                    instanceName,
                    agentVersion,
                    mcpVersion,
                    modelName = model.Name,
                    modelVersion = model.Version,
                    modelQuantization = model.Quantization
                },
                now));
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AgentHeartbeatResultView(
            instance.Id,
            instance.Status,
            now,
            true,
            settings.SuggestionsPaused,
            AgentPolicyDefaults.Version,
            AgentPolicyDefaults.HeartbeatIntervalSeconds);
    }

    public async Task<AgentTaskListPage> ListTasksAsync(
        AgentIdentity identity,
        string? instanceHeader,
        string? status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireEnabledAsync(cancellationToken);
        var instance = await RequireInstanceAsync(identity, instanceHeader, cancellationToken);
        var normalizedStatus = NormalizeTaskFilter(status);
        var normalizedLimit = Math.Clamp(limit, 1, 50);
        var now = timeProvider.GetUtcNow();
        var budget = await aiBudget.GetAsync(settings, cancellationToken);
        if (!settings.SuggestionsPaused && !budget.ContentBudgetExceeded)
        {
            await SynchronizeTasksAsync(now, budget.DailyContentBudget, cancellationToken);
        }

        await ReleaseExpiredLeasesAsync(now, cancellationToken);

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

            items.Add(ToSummary(task));
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
        await RequireEnabledAsync(cancellationToken);
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
            var released = await ReleaseTaskLeaseAsync(task.Id, now, "lease_expired", cancellationToken);
            if (released > 0)
            {
                await RecordLeaseExpiryAsync(released, now, cancellationToken);
            }
        }

        if (!await EnsureTaskFreshAsync(task, now, cancellationToken))
        {
            throw TaskNotFound();
        }

        var detail = await BuildTaskDetailAsync(task.Id, cancellationToken);
        return detail ?? throw TaskNotFound();
    }

    public async Task<AgentClaimResultView> ClaimTaskAsync(
        AgentIdentity identity,
        string? instanceHeader,
        Guid taskId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireEnabledAsync(cancellationToken);
        if (settings.SuggestionsPaused)
        {
            throw AgentDisabled();
        }

        var instance = await RequireInstanceAsync(identity, instanceHeader, cancellationToken);
        await EnsureNewWorkAllowedAsync(settings, identity, audit, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await ReleaseExpiredLeasesAsync(now, cancellationToken);
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

        var leaseExpiresAt = now.AddSeconds(AgentPolicyDefaults.LeaseSeconds);
        var leaseToken = Guid.NewGuid();
        var updated = await db.ModerationTasks
            .Where(candidate => candidate.Id == taskId
                && (candidate.Status == ModerationTaskStatuses.Queued
                    || (candidate.Status == ModerationTaskStatuses.Claimed
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt <= now)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, ModerationTaskStatuses.Claimed)
                    .SetProperty(candidate => candidate.ClaimedByAgentInstanceId, instance.Id)
                    .SetProperty(candidate => candidate.LeaseToken, leaseToken)
                    .SetProperty(candidate => candidate.ClaimedAt, now)
                    .SetProperty(candidate => candidate.LeaseExpiresAt, leaseExpiresAt)
                    .SetProperty(candidate => candidate.LastErrorCode, (string?)null),
                cancellationToken);
        if (updated == 0)
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
                leaseExpiresAt
            },
            now));
        await db.SaveChangesAsync(cancellationToken);
        var detail = await BuildTaskDetailAsync(taskId, cancellationToken)
            ?? throw TaskNotFound();
        return new AgentClaimResultView(detail, leaseExpiresAt);
    }

    public async Task<AgentReleaseResultView> ReleaseTaskAsync(
        AgentIdentity identity,
        string? instanceHeader,
        Guid taskId,
        AgentReleaseRequest? request,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await RequireEnabledAsync(cancellationToken);
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
        var released = await db.ModerationTasks
            .Where(candidate => candidate.Id == taskId
                && candidate.Status == ModerationTaskStatuses.Claimed
                && candidate.ClaimedByAgentInstanceId == instance.Id
                && candidate.LeaseToken == task.LeaseToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, ModerationTaskStatuses.Queued)
                    .SetProperty(candidate => candidate.ClaimedByAgentInstanceId, (Guid?)null)
                    .SetProperty(candidate => candidate.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                    .SetProperty(candidate => candidate.LastErrorCode, "released"),
                cancellationToken);
        if (released == 0)
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

    public async Task<(AgentSuggestionResultView Result, bool Replayed)> SubmitSuggestionAsync(
        AgentIdentity identity,
        string? instanceHeader,
        Guid taskId,
        AgentSuggestionRequest request,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var settings = await RequireEnabledAsync(cancellationToken);
        if (settings.SuggestionsPaused)
        {
            throw AgentDisabled();
        }

        var instance = await RequireInstanceAsync(identity, instanceHeader, cancellationToken);
        var suggestion = NormalizeSuggestion(request, settings);
        var existing = await db.ModerationSuggestions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.ServiceAccountId == identity.ServiceAccountId
                    && candidate.IdempotencyKey == suggestion.IdempotencyKey,
                cancellationToken);
        if (existing is not null)
        {
            if (existing.TaskId != taskId)
            {
                throw new ApiProblemException(
                    ApiErrorCodes.Conflict,
                    409,
                    "幂等键已用于其他任务。");
            }

            return (ToSuggestionResult(existing), true);
        }

        await EnsureNewWorkAllowedAsync(settings, identity, audit, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await db.ModerationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null)
        {
            throw TaskNotFound();
        }

        var snapshot = await GetCurrentSnapshotAsync(task, cancellationToken);
        if (snapshot is null)
        {
            await ExpireTaskAsync(task.Id, "content_missing", now, cancellationToken);
            throw TaskNotFound();
        }

        var currentHash = snapshot.Post is not null
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
        if (task.ContentVersion != snapshot.Version || task.ContentHash != currentHash)
        {
            await ExpireTaskAsync(task.Id, "content_stale", now, cancellationToken);
            throw new ApiProblemException(
                ApiErrorCodes.ContentVersionStale,
                409,
                "内容版本已变化，请重新领取任务后再提交建议。",
                new
                {
                    currentVersion = snapshot.Version,
                    submittedVersion = task.ContentVersion
                });
        }

        if (suggestion.ContentVersion != task.ContentVersion)
        {
            throw new ApiProblemException(
                ApiErrorCodes.ContentVersionStale,
                409,
                "内容版本已变化，请重新领取任务后再提交建议。",
                new
                {
                    currentVersion = task.ContentVersion,
                    submittedVersion = suggestion.ContentVersion
                });
        }

        if (task.Status != ModerationTaskStatuses.Claimed
            || task.ClaimedByAgentInstanceId != instance.Id
            || task.LeaseExpiresAt is null
            || task.LeaseExpiresAt <= now)
        {
            throw new ApiProblemException(
                ApiErrorCodes.TaskLeaseExpired,
                409,
                "任务租约已过期或不属于当前实例。");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(7210522)",
            cancellationToken);
        var currentBudget = await aiBudget.GetAsync(settings, cancellationToken);
        if (currentBudget.BudgetExceeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            await aiBudget.RecordBudgetBlockAsync(audit, currentBudget, cancellationToken);
            throw AiBudgetService.BudgetExceeded(currentBudget);
        }

        if (currentBudget.TaskBudgetExceeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            await aiBudget.RecordBudgetBlockAsync(
                audit,
                currentBudget,
                "daily_task_budget_exceeded",
                AiBudgetService.TaskBudgetExceededAction,
                cancellationToken);
            throw AiBudgetService.TaskBudgetExceeded(currentBudget);
        }

        var currentAccountBudget = await aiBudget.GetAccountStatusAsync(
            identity.ServiceAccountId,
            cancellationToken);
        if (currentAccountBudget.BudgetExceeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            await aiBudget.RecordAccountBudgetBlockAsync(audit, currentAccountBudget, cancellationToken);
            throw AiBudgetService.AccountBudgetExceeded(currentAccountBudget);
        }

        var claimed = await db.ModerationTasks
            .Where(candidate => candidate.Id == taskId
                && candidate.Status == ModerationTaskStatuses.Claimed
                && candidate.ClaimedByAgentInstanceId == instance.Id
                && candidate.LeaseExpiresAt != null
                && candidate.LeaseExpiresAt > now
                && candidate.LeaseToken == task.LeaseToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, ModerationTaskStatuses.Suggested)
                    .SetProperty(candidate => candidate.SuggestedAt, now)
                    .SetProperty(candidate => candidate.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.LeaseToken, (Guid?)null),
                cancellationToken);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ApiProblemException(
                ApiErrorCodes.TaskLeaseExpired,
                409,
                "任务租约已过期或已被人工裁决。");
        }

        var entity = new ModerationSuggestion
        {
            TaskId = taskId,
            ServiceAccountId = identity.ServiceAccountId,
            AgentInstanceId = instance.Id,
            Suggestion = suggestion.Suggestion,
            PolicyCode = suggestion.PolicyCode,
            Reason = suggestion.Reason,
            Confidence = suggestion.Confidence,
            RuntimeLocation = RuntimeLocations.Local,
            ModelRuntime = RuntimeLocations.Local,
            ModelName = suggestion.Model.Name!,
            ModelVersion = suggestion.Model.Version!,
            ModelQuantization = suggestion.Model.Quantization!,
            PromptVersion = suggestion.PromptVersion,
            PolicyVersion = suggestion.PolicyVersion,
            LatencyMs = request.LatencyMs,
            TokenUsage = JsonSerializer.SerializeToDocument(request.TokenUsage ?? new Dictionary<string, long>()),
            IdempotencyKey = suggestion.IdempotencyKey,
            CreatedAt = now
        };
        db.ModerationSuggestions.Add(entity);
        db.AuditLogs.Add(AuditLogWriter.CreateServiceAction(
            audit,
            "agent.suggestion_submitted",
            "moderation_task",
            taskId,
            new
            {
                suggestionId = entity.Id,
                suggestion = entity.Suggestion,
                policyCode = entity.PolicyCode,
                confidence = entity.Confidence,
                contentVersion = task.ContentVersion,
                policyVersion = entity.PolicyVersion,
                modelName = entity.ModelName,
                modelVersion = entity.ModelVersion,
                modelQuantization = entity.ModelQuantization,
                instanceId = instance.Id,
                latencyMs = entity.LatencyMs
            },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var replayed = await db.ModerationSuggestions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.ServiceAccountId == identity.ServiceAccountId
                        && candidate.IdempotencyKey == suggestion.IdempotencyKey,
                    cancellationToken);
            if (replayed is not null && replayed.TaskId == taskId)
            {
                return (ToSuggestionResult(replayed), true);
            }

            throw new ApiProblemException(
                ApiErrorCodes.Conflict,
                409,
                "幂等键冲突，请使用新的幂等键重试。");
        }

        return (ToSuggestionResult(entity), false);
    }

    private async Task<AiSettingsData> RequireEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await aiSettings.GetAsync(cancellationToken);
        if (!settings.Enabled)
        {
            throw AgentDisabled();
        }

        return settings;
    }

    private async Task EnsureNewWorkAllowedAsync(
        AiSettingsData settings,
        AgentIdentity identity,
        AuditContext audit,
        CancellationToken cancellationToken)
    {
        var budget = await aiBudget.GetAsync(settings, cancellationToken);
        if (budget.BudgetExceeded)
        {
            await aiBudget.RecordBudgetBlockAsync(audit, budget, cancellationToken);
            throw AiBudgetService.BudgetExceeded(budget);
        }

        if (budget.TaskBudgetExceeded)
        {
            await aiBudget.RecordBudgetBlockAsync(
                audit,
                budget,
                "daily_task_budget_exceeded",
                AiBudgetService.TaskBudgetExceededAction,
                cancellationToken);
            throw AiBudgetService.TaskBudgetExceeded(budget);
        }

        var accountBudget = await aiBudget.GetAccountStatusAsync(
            identity.ServiceAccountId,
            cancellationToken);
        if (accountBudget.BudgetExceeded)
        {
            await aiBudget.RecordAccountBudgetBlockAsync(audit, accountBudget, cancellationToken);
            throw AiBudgetService.AccountBudgetExceeded(accountBudget);
        }

        if (budget.FailureCircuitOpen)
        {
            logger.LogWarning(
                "ai_failure_circuit_open serviceAccountId={ServiceAccountId} recentFailures={RecentFailures} threshold={Threshold} windowMinutes={WindowMinutes}",
                identity.ServiceAccountId,
                budget.RecentFailures,
                AiBudgetService.FailureThreshold,
                AiBudgetService.FailureWindowMinutes);
            throw AiBudgetService.FailureCircuitOpen(budget);
        }
    }

    private async Task<AgentInstance> RequireInstanceAsync(
        AgentIdentity identity,
        string? instanceHeader,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(instanceHeader, out var instanceId))
        {
            throw ApiProblemException.Validation(
                "X-Agent-Instance-Id",
                "缺少或无效的 Agent 实例标识。");
        }

        var instance = await db.AgentInstances
            .SingleOrDefaultAsync(
                candidate => candidate.Id == instanceId
                    && candidate.ServiceAccountId == identity.ServiceAccountId,
                cancellationToken);
        if (instance is null)
        {
            throw ApiProblemException.Forbidden(
                "instance_mismatch",
                "Agent 实例不属于当前服务账号。");
        }

        if (instance.Status != AgentInstanceStatuses.Active)
        {
            throw ApiProblemException.Forbidden(
                instance.Status == AgentInstanceStatuses.Revoked ? "instance_revoked" : "instance_paused",
                "Agent 实例不可用。");
        }

        return instance;
    }

    private async Task SynchronizeTasksAsync(
        DateTimeOffset now,
        int contentBudget,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(7210523)",
            cancellationToken);
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var createdToday = await db.ModerationTasks
            .AsNoTracking()
            .CountAsync(
                task => task.CreatedAt >= dayStart && task.CreatedAt < dayStart.AddDays(1),
                cancellationToken);
        var remaining = Math.Max(0, contentBudget - createdToday);
        if (remaining == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var pendingPosts = await db.Posts
            .AsNoTracking()
            .Where(post => post.ModerationStatus == ModerationStatuses.Pending
                && post.DeletedAt == null
                && !db.ModerationTasks.Any(task =>
                    task.TargetType == ModerationTargetTypes.Post
                    && task.TargetId == post.Id
                    && task.ContentVersion == post.Version
                    && ActiveTaskStatuses.Contains(task.Status)))
            .OrderBy(post => post.CreatedAt)
            .Take(50)
            .Select(post => new
            {
                post.Id,
                post.Version,
                post.Title,
                post.BodyMd,
                post.CategoryId,
                post.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var pendingComments = await db.Comments
            .AsNoTracking()
            .Where(comment => comment.ModerationStatus == ModerationStatuses.Pending
                && comment.DeletedAt == null
                && !db.ModerationTasks.Any(task =>
                    task.TargetType == ModerationTargetTypes.Comment
                    && task.TargetId == comment.Id
                    && task.ContentVersion == comment.Version
                    && ActiveTaskStatuses.Contains(task.Status)))
            .OrderBy(comment => comment.CreatedAt)
            .Take(50)
            .Select(comment => new
            {
                comment.Id,
                comment.Version,
                comment.PostId,
                comment.ParentId,
                comment.BodyMd,
                comment.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var postIds = pendingPosts.Select(post => post.Id).ToList();
        var commentIds = pendingComments.Select(comment => comment.Id).ToList();
        var tasks = await db.ModerationTasks
            .Where(task => (task.TargetType == ModerationTargetTypes.Post && postIds.Contains(task.TargetId))
                || (task.TargetType == ModerationTargetTypes.Comment && commentIds.Contains(task.TargetId)))
            .Where(task => ActiveTaskStatuses.Contains(task.Status))
            .ToListAsync(cancellationToken);

        var created = false;
        var createdCount = 0;
        foreach (var post in pendingPosts)
        {
            if (createdCount >= remaining)
            {
                break;
            }

            var hash = AgentContentHash.ForPost(post.Id, post.Version, post.Title, post.BodyMd, post.CategoryId);
            var active = tasks
                .Where(task => task.TargetType == ModerationTargetTypes.Post && task.TargetId == post.Id)
                .ToList();
            var current = active.FirstOrDefault(task =>
                task.ContentVersion == post.Version && task.ContentHash == hash);
            if (current is not null)
            {
                continue;
            }

            foreach (var stale in active)
            {
                stale.Status = ModerationTaskStatuses.Expired;
                stale.CompletedAt = now;
                stale.LeaseExpiresAt = null;
                stale.LeaseToken = null;
                stale.LastErrorCode = "content_stale";
            }

            var riskHints = await LoadRiskHintsAsync(
                ModerationTargetTypes.Post,
                post.Id,
                post.Version,
                cancellationToken);
            var reportCount = await db.Reports
                .AsNoTracking()
                .CountAsync(report => report.TargetType == ModerationTargetTypes.Post
                    && report.TargetId == post.Id
                    && report.Status == ReportStatuses.Pending, cancellationToken);
            db.ModerationTasks.Add(NewTask(
                ModerationTargetTypes.Post,
                post.Id,
                post.Version,
                hash,
                riskHints,
                reportCount,
                now));
            created = true;
            createdCount++;
        }

        foreach (var comment in pendingComments)
        {
            if (createdCount >= remaining)
            {
                break;
            }

            var hash = AgentContentHash.ForComment(
                comment.Id,
                comment.Version,
                comment.PostId,
                comment.ParentId,
                comment.BodyMd);
            var active = tasks
                .Where(task => task.TargetType == ModerationTargetTypes.Comment && task.TargetId == comment.Id)
                .ToList();
            var current = active.FirstOrDefault(task =>
                task.ContentVersion == comment.Version && task.ContentHash == hash);
            if (current is not null)
            {
                continue;
            }

            foreach (var stale in active)
            {
                stale.Status = ModerationTaskStatuses.Expired;
                stale.CompletedAt = now;
                stale.LeaseExpiresAt = null;
                stale.LeaseToken = null;
                stale.LastErrorCode = "content_stale";
            }

            var riskHints = await LoadRiskHintsAsync(
                ModerationTargetTypes.Comment,
                comment.Id,
                comment.Version,
                cancellationToken);
            var reportCount = await db.Reports
                .AsNoTracking()
                .CountAsync(report => report.TargetType == ModerationTargetTypes.Comment
                    && report.TargetId == comment.Id
                    && report.Status == ReportStatuses.Pending, cancellationToken);
            db.ModerationTasks.Add(NewTask(
                ModerationTargetTypes.Comment,
                comment.Id,
                comment.Version,
                hash,
                riskHints,
                reportCount,
                now));
            created = true;
            createdCount++;
        }

        if (created || db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                logger.LogInformation("Concurrent Agent task synchronization hit a unique task; continuing.");
                return;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> EnsureTaskFreshAsync(
        ModerationTask task,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetCurrentSnapshotAsync(task, cancellationToken);
        if (snapshot is null)
        {
            await ExpireTaskAsync(task.Id, "content_missing", now, cancellationToken);
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
            await ExpireTaskAsync(task.Id, "content_stale", now, cancellationToken);
            return false;
        }

        return true;
    }

    private async Task<AgentContentSnapshot?> GetCurrentSnapshotAsync(
        ModerationTask task,
        CancellationToken cancellationToken)
    {
        if (task.TargetType == ModerationTargetTypes.Post)
        {
            var post = await db.Posts
                .AsNoTracking()
                .Where(candidate => candidate.Id == task.TargetId
                    && candidate.ModerationStatus == ModerationStatuses.Pending
                    && candidate.DeletedAt == null)
                .Select(candidate => new AgentPostSnapshot(
                    candidate.Id,
                    candidate.Version,
                    candidate.Title,
                    candidate.BodyMd,
                    candidate.CategoryId))
                .SingleOrDefaultAsync(cancellationToken);
            return post is null ? null : new AgentContentSnapshot(post, null, post.Version);
        }

        var comment = await db.Comments
            .AsNoTracking()
            .Where(candidate => candidate.Id == task.TargetId
                && candidate.ModerationStatus == ModerationStatuses.Pending
                && candidate.DeletedAt == null)
            .Select(candidate => new AgentCommentSnapshot(
                candidate.Id,
                candidate.Version,
                candidate.PostId,
                candidate.ParentId,
                candidate.BodyMd))
            .SingleOrDefaultAsync(cancellationToken);
        return comment is null ? null : new AgentContentSnapshot(null, comment, comment.Version);
    }

    private async Task<AgentTaskDetailView?> BuildTaskDetailAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await db.ModerationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var riskHints = ReadRiskHints(task.RiskHintsJson);
        AgentTaskContentView? content;
        if (task.TargetType == ModerationTargetTypes.Post)
        {
            var post = await db.Posts
                .AsNoTracking()
                .Where(candidate => candidate.Id == task.TargetId
                    && candidate.ModerationStatus == ModerationStatuses.Pending
                    && candidate.DeletedAt == null)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.Version,
                    candidate.Title,
                    candidate.BodyMd,
                    candidate.CategoryId
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (post is null)
            {
                return null;
            }

            var category = await db.Categories
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == post.CategoryId, cancellationToken);
            var tagRows = await (
                    from postTag in db.PostTags.AsNoTracking()
                    join tag in db.Tags.AsNoTracking() on postTag.TagId equals tag.Id
                    where postTag.PostId == post.Id && tag.Status == TagStatuses.Active
                    orderby tag.Name
                    select new { tag.Slug, tag.Name })
                .ToListAsync(cancellationToken);
            var artifactRows = await db.Artifacts
                .AsNoTracking()
                .Where(artifact => artifact.PostId == post.Id)
                .OrderBy(artifact => artifact.SortOrder)
                .ThenBy(artifact => artifact.Id)
                .Take(AgentPolicyDefaults.MaxArtifacts)
                .Select(artifact => new
                {
                    artifact.Kind,
                    artifact.Name,
                    artifact.Note,
                    artifact.Content
                })
                .ToListAsync(cancellationToken);
            var imageRows = await db.PostImages
                .AsNoTracking()
                .Where(image => image.PostId == post.Id)
                .OrderBy(image => image.SortOrder)
                .ThenBy(image => image.Id)
                .Take(AgentPolicyDefaults.MaxImages)
                .Select(image => new { image.Url, image.AltText })
                .ToListAsync(cancellationToken);

            var redactions = new HashSet<string>(StringComparer.Ordinal);
            var title = RedactInto(post.Title, 200, redactions);
            var body = RedactInto(post.BodyMd, AgentPolicyDefaults.MaxBodyChars, redactions);
            var artifacts = artifactRows
                .Select(artifact =>
                {
                    var artifactContent = RedactInto(
                        artifact.Content,
                        AgentContentRedactor.MaxArtifactChars,
                        redactions);
                    var note = RedactInto(artifact.Note, 200, redactions);
                    var name = RedactInto(artifact.Name, 200, redactions);
                    return new AgentTaskArtifactView(
                        artifact.Kind,
                        name.Text,
                        note.Text,
                        artifactContent.Text,
                        artifactContent.Redacted || note.Redacted || name.Redacted,
                        artifactContent.Redacted || note.Redacted || name.Redacted
                            ? "content_redacted"
                            : artifactContent.Truncated
                                ? "content_truncated"
                                : null);
                })
                .ToList();
            var images = imageRows
                .Select(image => new AgentTaskImageView(
                    image.Url,
                    RedactInto(image.AltText, 200, redactions).Text))
                .ToList();
            content = new AgentTaskContentView(
                true,
                title.Text,
                body.Text,
                new CategoryView(
                    category.Id,
                    category.Slug,
                    category.Name,
                    category.Description,
                    category.DisplayOrder,
                    category.Status),
                tagRows.Select(tag => new TagSummaryView(tag.Slug, tag.Name)).ToList(),
                artifacts,
                images,
                riskHints,
                redactions.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                AgentPolicyDefaults.UntrustedContentNotice);
        }
        else
        {
            var comment = await db.Comments
                .AsNoTracking()
                .Where(candidate => candidate.Id == task.TargetId
                    && candidate.ModerationStatus == ModerationStatuses.Pending
                    && candidate.DeletedAt == null)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.PostId,
                    candidate.BodyMd
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (comment is null)
            {
                return null;
            }

            var post = await db.Posts
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == comment.PostId, cancellationToken);
            if (post.DeletedAt is not null)
            {
                return null;
            }

            var category = await db.Categories
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == post.CategoryId, cancellationToken);
            var tagRows = await (
                    from postTag in db.PostTags.AsNoTracking()
                    join tag in db.Tags.AsNoTracking() on postTag.TagId equals tag.Id
                    where postTag.PostId == post.Id && tag.Status == TagStatuses.Active
                    orderby tag.Name
                    select new { tag.Slug, tag.Name })
                .ToListAsync(cancellationToken);
            var redactions = new HashSet<string>(StringComparer.Ordinal);
            var body = RedactInto(comment.BodyMd, AgentPolicyDefaults.MaxBodyChars, redactions);
            content = new AgentTaskContentView(
                true,
                "评论",
                body.Text,
                new CategoryView(
                    category.Id,
                    category.Slug,
                    category.Name,
                    category.Description,
                    category.DisplayOrder,
                    category.Status),
                tagRows.Select(tag => new TagSummaryView(tag.Slug, tag.Name)).ToList(),
                [],
                [],
                riskHints,
                redactions.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                AgentPolicyDefaults.UntrustedContentNotice);
        }

        return new AgentTaskDetailView(
            task.Id,
            task.TargetType,
            task.TargetId,
            task.ContentVersion,
            task.ContentHash,
            task.Status,
            task.Priority,
            riskHints,
            task.CreatedAt,
            task.LeaseExpiresAt,
            content,
            AgentPolicyDefaults.Version);
    }

    private async Task ReleaseExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hasExpired = await db.ModerationTasks
            .AsNoTracking()
            .AnyAsync(
                task => task.Status == ModerationTaskStatuses.Claimed
                    && task.LeaseExpiresAt != null
                    && task.LeaseExpiresAt <= now,
                cancellationToken);
        if (!hasExpired)
        {
            return;
        }

        var released = await db.ModerationTasks
            .Where(task => task.Status == ModerationTaskStatuses.Claimed
                && task.LeaseExpiresAt != null
                && task.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.Status, ModerationTaskStatuses.Queued)
                    .SetProperty(task => task.ClaimedByAgentInstanceId, (Guid?)null)
                    .SetProperty(task => task.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseToken, (Guid?)null)
                    .SetProperty(task => task.LastErrorCode, "lease_expired"),
                cancellationToken);
        if (released > 0)
        {
            await RecordLeaseExpiryAsync(released, now, cancellationToken);
        }
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

    private async Task<int> ReleaseTaskLeaseAsync(
        Guid taskId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        return await db.ModerationTasks
            .Where(task => task.Id == taskId
                && task.Status == ModerationTaskStatuses.Claimed
                && task.LeaseExpiresAt != null
                && task.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.Status, ModerationTaskStatuses.Queued)
                    .SetProperty(task => task.ClaimedByAgentInstanceId, (Guid?)null)
                    .SetProperty(task => task.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseToken, (Guid?)null)
                    .SetProperty(task => task.LastErrorCode, reason),
                cancellationToken);
    }

    private Task ExpireTaskAsync(
        Guid taskId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return db.ModerationTasks
            .Where(task => task.Id == taskId && ActiveTaskStatuses.Contains(task.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.Status, ModerationTaskStatuses.Expired)
                    .SetProperty(task => task.CompletedAt, now)
                    .SetProperty(task => task.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseToken, (Guid?)null)
                    .SetProperty(task => task.LastErrorCode, reason),
                cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadRiskHintsAsync(
        string targetType,
        Guid targetId,
        int contentVersion,
        CancellationToken cancellationToken)
    {
        var document = await db.ModerationChecks
            .AsNoTracking()
            .Where(check => check.TargetType == targetType
                && check.TargetId == targetId
                && check.ContentVersion == contentVersion)
            .OrderByDescending(check => check.CreatedAt)
            .ThenByDescending(check => check.Id)
            .Select(check => check.RiskLabels)
            .FirstOrDefaultAsync(cancellationToken);
        return document is null ? [] : ReadRiskHints(document);
    }

    private static IReadOnlyList<string> ReadRiskHints(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
    }

    private static ModerationTask NewTask(
        string targetType,
        Guid targetId,
        int contentVersion,
        string contentHash,
        IReadOnlyList<string> riskHints,
        int reportCount,
        DateTimeOffset now)
    {
        var priority = riskHints.Count > 0 ? 80 : reportCount > 0 ? 70 : 40;
        return new ModerationTask
        {
            TargetType = targetType,
            TargetId = targetId,
            ContentVersion = contentVersion,
            ContentHash = contentHash,
            Status = ModerationTaskStatuses.Queued,
            Priority = (short)priority,
            RiskHintsJson = JsonSerializer.SerializeToDocument(riskHints),
            CreatedAt = now,
            TaskExpiresAt = now.AddDays(7)
        };
    }

    private static AgentTaskSummaryView ToSummary(ModerationTask task)
    {
        return new AgentTaskSummaryView(
            task.Id,
            task.TargetType,
            task.TargetId,
            task.ContentVersion,
            task.ContentHash,
            task.Status,
            task.Priority,
            ReadRiskHints(task.RiskHintsJson),
            task.CreatedAt,
            task.LeaseExpiresAt);
    }

    private static AgentSuggestionResultView ToSuggestionResult(ModerationSuggestion suggestion)
    {
        return new AgentSuggestionResultView(
            suggestion.Id,
            suggestion.TaskId,
            "recorded",
            ModerationStatuses.Pending,
            false,
            suggestion.IdempotencyKey,
            suggestion.CreatedAt);
    }

    private static AgentPolicyView ToPolicy(AiSettingsData settings)
    {
        return new AgentPolicyView(
            settings.Enabled,
            settings.SuggestionsPaused,
            AgentPolicyDefaults.Version,
            AgentPolicyDefaults.SuggestionTypes,
            AgentPolicyDefaults.Categories
                .Select(category => new AgentPolicyCategoryView(category.Code, category.Name))
                .ToList(),
            AgentPolicyDefaults.LeaseSeconds,
            settings.ApprovedModels
                .Select(model => new AgentModelAllowlistView(
                    model.Name,
                    model.Version,
                    model.Quantization))
                .ToList(),
            new AgentContentLimitsView(
                AgentPolicyDefaults.MaxBodyChars,
                AgentPolicyDefaults.MaxArtifacts,
                AgentPolicyDefaults.MaxImages),
            AgentPolicyDefaults.UntrustedContentNotice);
    }

    private static bool IsModelApproved(AiSettingsData settings, AgentModelInfo model)
    {
        return settings.ApprovedModels.Any(approved =>
            string.Equals(approved.Name, model.Name, StringComparison.Ordinal)
            && string.Equals(approved.Version, model.Version, StringComparison.Ordinal)
            && string.Equals(approved.Quantization, model.Quantization, StringComparison.Ordinal));
    }

    private static AgentModelInfo NormalizeModel(AgentModelInfo? model, string field)
    {
        if (model is null)
        {
            throw SuggestionOrValidationFailure(field, "必须提供本地模型信息。");
        }

        var name = NormalizeText(model.Name, 80, $"{field}.name", required: true);
        var version = NormalizeText(model.Version, 40, $"{field}.version", required: true);
        var quantization = NormalizeText(model.Quantization, 40, $"{field}.quantization", required: true);
        var runtime = NormalizeText(model.Runtime, 20, $"{field}.runtime", required: true);
        return new AgentModelInfo(name, version, quantization, runtime);
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

    private static AgentSuggestionInput NormalizeSuggestion(
        AgentSuggestionRequest request,
        AiSettingsData settings)
    {
        if (!SuggestionTypes.All.Contains(request.Suggestion ?? string.Empty, StringComparer.Ordinal))
        {
            throw SuggestionFailure("suggestion", "suggestion 必须是 approve、reject 或 escalate。");
        }

        var policyCode = NormalizeText(request.PolicyCode, 80, "policyCode", required: true);
        var reason = NormalizeText(request.Reason, 2000, "reason", required: true);
        if (request.Confidence is < 0 or > 1)
        {
            throw SuggestionFailure("confidence", "confidence 必须在 0 到 1 之间。");
        }

        if (request.ContentVersion < 1)
        {
            throw SuggestionFailure("contentVersion", "contentVersion 必须大于 0。");
        }

        var policyVersion = NormalizeText(request.PolicyVersion, 40, "policyVersion", required: true);
        if (!string.Equals(policyVersion, AgentPolicyDefaults.Version, StringComparison.Ordinal))
        {
            throw new ApiProblemException(
                ApiErrorCodes.SuggestionInvalid,
                400,
                "策略版本已过期，请刷新策略后重试。",
                new { currentPolicyVersion = AgentPolicyDefaults.Version });
        }

        var model = NormalizeModel(request.Model, "model");
        var modelRuntime = NormalizeText(request.ModelRuntime, 20, "modelRuntime", required: true);
        var runtimeLocation = NormalizeText(request.RuntimeLocation, 20, "runtimeLocation", required: true);
        if (modelRuntime != RuntimeLocations.Local
            || runtimeLocation != RuntimeLocations.Local
            || !IsModelApproved(settings, model))
        {
            throw new ApiProblemException(
                ApiErrorCodes.Forbidden,
                403,
                "模型或运行位置未获批准。",
                new { reason = "model_or_runtime_unapproved" });
        }

        var promptVersion = NormalizeText(request.PromptVersion, 40, "promptVersion", required: true);
        var idempotencyKey = NormalizeText(request.IdempotencyKey, 100, "idempotencyKey", required: true);
        if (idempotencyKey.Length < 8)
        {
            throw SuggestionFailure("idempotencyKey", "idempotencyKey 至少 8 个字符。");
        }

        if (request.LatencyMs is < 0)
        {
            throw SuggestionFailure("latencyMs", "latencyMs 不能为负数。");
        }

        if (request.TokenUsage is not null)
        {
            if (request.TokenUsage.Count > 50
                || request.TokenUsage.Any(pair =>
                    pair.Key.Length > 40
                    || pair.Value < 0
                    || pair.Key.Any(char.IsControl)))
            {
                throw SuggestionFailure("tokenUsage", "tokenUsage 必须是非负整数字典。");
            }
        }

        return new AgentSuggestionInput(
            request.Suggestion!,
            policyCode,
            reason,
            request.Confidence,
            request.ContentVersion,
            policyVersion,
            model,
            promptVersion,
            idempotencyKey);
    }

    private static ApiProblemException AgentDisabled()
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

    private static AgentRedactionResult RedactInto(
        string? value,
        int maxChars,
        HashSet<string> redactions)
    {
        var result = AgentContentRedactor.Redact(value, maxChars);
        foreach (var category in result.Categories)
        {
            redactions.Add(category);
        }

        return result;
    }

    private sealed record AgentPostSnapshot(
        Guid Id,
        int Version,
        string Title,
        string BodyMd,
        Guid CategoryId);

    private sealed record AgentCommentSnapshot(
        Guid Id,
        int Version,
        Guid PostId,
        Guid? ParentId,
        string BodyMd);

    private sealed record AgentContentSnapshot(
        AgentPostSnapshot? Post,
        AgentCommentSnapshot? Comment,
        int Version);

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