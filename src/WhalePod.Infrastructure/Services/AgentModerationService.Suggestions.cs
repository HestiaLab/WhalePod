using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using WhalePod.Domain;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed partial class AgentModerationService
{
    public async Task<(AgentSuggestionResultView Result, bool Replayed)> SubmitSuggestionAsync(
        AgentIdentity identity,
        string? instanceHeader,
        Guid taskId,
        AgentSuggestionRequest request,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var settings = await budgetGate.RequireEnabledAsync(cancellationToken);
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

        await budgetGate.EnsureNewWorkAllowedAsync(settings, identity, audit, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var task = await db.ModerationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null)
        {
            throw TaskNotFound();
        }

        var snapshot = await projector.GetCurrentSnapshotAsync(task, cancellationToken);
        if (snapshot is null)
        {
            await leaseService.ExpireAsync(task.Id, "content_missing", now, cancellationToken);
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
            await leaseService.ExpireAsync(task.Id, "content_stale", now, cancellationToken);
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

        var claimed = await leaseService.TryMarkSuggestedAsync(
            taskId,
            instance.Id,
            task.LeaseToken,
            now,
            cancellationToken);
        if (!claimed)
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
}