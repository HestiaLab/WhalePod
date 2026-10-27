using System.Net;
using System.Text.Json;

using WhalePod.Domain;

namespace WhalePod.Domain.Platform;

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string ActorType { get; set; } = AuditActorTypes.System;
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public string? RequestId { get; set; }
    public byte[]? IpHash { get; set; }
    public byte[]? UserAgentHash { get; set; }
    public JsonDocument MetadataJson { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SiteSetting
{
    public string Key { get; set; } = string.Empty;
    public JsonDocument Value { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

public sealed class ServiceAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public IPAddress[]? IpAllowlist { get; set; }
    public int? DailyTokenBudget { get; set; }
    public string Status { get; set; } = ServiceAccountStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
}

public sealed class ServiceToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ServiceAccountId { get; set; }
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();
    public string Prefix { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AgentInstance
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ServiceAccountId { get; set; }
    public string InstanceName { get; set; } = string.Empty;
    public string HostFingerprint { get; set; } = string.Empty;
    public string RuntimeLocation { get; set; } = RuntimeLocations.Local;
    public string AgentVersion { get; set; } = string.Empty;
    public string? McpVersion { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string ModelQuantization { get; set; } = string.Empty;
    public string ModelRuntime { get; set; } = RuntimeLocations.Local;
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public int QueueDepth { get; set; }
    public string Status { get; set; } = AgentInstanceStatuses.Active;
    public string? StatusReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}