namespace WhalePod.Infrastructure.Services;

public sealed record AgentIdentity(
    Guid ServiceAccountId,
    string Name,
    IReadOnlyList<string> Scopes);

public sealed record AgentModelInfo(
    string? Name,
    string? Version,
    string? Quantization,
    string? Runtime);

public sealed record AgentHeartbeatRequest(
    string? InstanceName,
    string? HostFingerprint,
    string? AgentVersion,
    string? McpVersion,
    string? RuntimeLocation,
    AgentModelInfo? Model,
    int QueueDepth,
    DateTimeOffset? LastProcessedAt);

public sealed record AgentSuggestionRequest(
    string? Suggestion,
    string? PolicyCode,
    string? Reason,
    decimal Confidence,
    int ContentVersion,
    string? PolicyVersion,
    AgentModelInfo? Model,
    string? ModelRuntime,
    string? RuntimeLocation,
    string? PromptVersion,
    string? IdempotencyKey,
    int? LatencyMs,
    Dictionary<string, long>? TokenUsage);

public sealed record AgentReleaseRequest(string? Reason);

public sealed record AgentTaskArtifactView(
    string Kind,
    string Name,
    string? Note,
    string? Content,
    bool Redacted,
    string? RedactionReason);

public sealed record AgentTaskImageView(string Url, string? AltText);

public sealed record AgentTaskContentView(
    bool UntrustedContent,
    string Title,
    string Body,
    CategoryView Category,
    IReadOnlyList<TagSummaryView> Tags,
    IReadOnlyList<AgentTaskArtifactView> Artifacts,
    IReadOnlyList<AgentTaskImageView> Images,
    IReadOnlyList<string> RiskHints,
    IReadOnlyList<string> Redactions,
    string ContentNotice);

public sealed record AgentTaskSummaryView(
    Guid Id,
    string TargetType,
    Guid TargetId,
    int ContentVersion,
    string ContentHash,
    string Status,
    int Priority,
    IReadOnlyList<string> RiskHints,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LeaseExpiresAt);

public sealed record AgentTaskDetailView(
    Guid Id,
    string TargetType,
    Guid TargetId,
    int ContentVersion,
    string ContentHash,
    string Status,
    int Priority,
    IReadOnlyList<string> RiskHints,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LeaseExpiresAt,
    AgentTaskContentView Content,
    string PolicyVersion);

public sealed record AgentTaskListPage(
    IReadOnlyList<AgentTaskSummaryView> Items,
    string? NextCursor);

public sealed record AgentClaimResultView(AgentTaskDetailView Task, DateTimeOffset LeaseExpiresAt);

public sealed record AgentSuggestionResultView(
    Guid SuggestionId,
    Guid TaskId,
    string Status,
    string ContentStatus,
    bool HumanDecided,
    string IdempotencyKey,
    DateTimeOffset CreatedAt);

public sealed record AgentReleaseResultView(bool Released, Guid TaskId, string TaskStatus);

public sealed record AgentHeartbeatResultView(
    Guid InstanceId,
    string Status,
    DateTimeOffset ServerTime,
    bool AiEnabled,
    bool SuggestionsPaused,
    string PolicyVersion,
    int HeartbeatIntervalSeconds);

public sealed record AgentPolicyCategoryView(string Code, string Name);

public sealed record AgentModelAllowlistView(string Name, string? Version, string? Quantization);

public sealed record AgentContentLimitsView(int MaxBodyChars, int MaxArtifacts, int MaxImages);

public sealed record AgentPolicyView(
    bool Enabled,
    bool SuggestionsPaused,
    string PolicyVersion,
    IReadOnlyList<string> SuggestionTypes,
    IReadOnlyList<AgentPolicyCategoryView> Categories,
    int LeaseSeconds,
    IReadOnlyList<AgentModelAllowlistView> ModelAllowlist,
    AgentContentLimitsView ContentLimits,
    string UntrustedContentNotice);