using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using WhalePod.Domain;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed partial class AgentModerationService
{
    public async Task<AgentPolicyView> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await budgetGate.RequireEnabledAsync(cancellationToken);
        return ToPolicy(settings);
    }

    public async Task<AgentHeartbeatResultView> HeartbeatAsync(
        AgentIdentity identity,
        AgentHeartbeatRequest request,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var settings = await budgetGate.RequireEnabledAsync(cancellationToken);
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
}