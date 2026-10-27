using System.Text.Json;

using WhalePod.Domain;

namespace WhalePod.Domain.Moderation;

public sealed class ModerationAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string TargetType { get; set; } = ModerationTargetTypes.Post;
    public Guid TargetId { get; set; }
    public string ActorType { get; set; } = ModerationActorTypes.User;
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string FromStatus { get; set; } = string.Empty;
    public string ToStatus { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public int ContentVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ModerationTask
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string TargetType { get; set; } = ModerationTargetTypes.Post;
    public Guid TargetId { get; set; }
    public int ContentVersion { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string Status { get; set; } = ModerationTaskStatuses.Queued;
    public short Priority { get; set; }
    public JsonDocument RiskHintsJson { get; set; } = JsonDocument.Parse("[]");
    public Guid? ClaimedByAgentInstanceId { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? TaskExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SuggestedAt { get; set; }
    public DateTimeOffset? HumanDecidedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastErrorCode { get; set; }
}

public sealed class ModerationSuggestion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TaskId { get; set; }
    public Guid ServiceAccountId { get; set; }
    public Guid AgentInstanceId { get; set; }
    public string Suggestion { get; set; } = SuggestionTypes.Escalate;
    public string PolicyCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public string RuntimeLocation { get; set; } = RuntimeLocations.Local;
    public string ModelRuntime { get; set; } = RuntimeLocations.Local;
    public string ModelName { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string ModelQuantization { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public int? LatencyMs { get; set; }
    public JsonDocument TokenUsage { get; set; } = JsonDocument.Parse("{}");
    public string? HumanDecision { get; set; }
    public Guid? HumanDecisionBy { get; set; }
    public string? HumanDecisionReason { get; set; }
    public DateTimeOffset? HumanDecisionAt { get; set; }
    public Guid? ModerationActionId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Appeal
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AppellantId { get; set; }
    public string TargetType { get; set; } = AppealTargetTypes.Post;
    public Guid TargetId { get; set; }
    public Guid? RelatedActionId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = AppealStatuses.Pending;
    public Guid? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class ModerationCheck
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string TargetType { get; set; } = ModerationTargetTypes.Post;
    public Guid TargetId { get; set; }
    public int ContentVersion { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string CheckType { get; set; } = ModerationCheckTypes.Text;
    public string Result { get; set; } = ModerationCheckResults.Review;
    public JsonDocument RiskLabels { get; set; } = JsonDocument.Parse("[]");
    public JsonDocument? RawResponse { get; set; }
    public int? LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SensitiveWord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Term { get; set; } = string.Empty;
    public string Action { get; set; } = SensitiveWordActions.Manual;
    public string Status { get; set; } = SensitiveWordStatuses.Active;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}