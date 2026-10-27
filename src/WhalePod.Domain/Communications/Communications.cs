using System.Text.Json;

using WhalePod.Domain;

namespace WhalePod.Domain.Communications;

public sealed class EmailOutbox
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string ToAddress { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string Template { get; set; } = string.Empty;
    public JsonDocument PayloadJson { get; set; } = JsonDocument.Parse("{}");
    public string Status { get; set; } = EmailStatuses.Pending;
    public short Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedUntil { get; set; }
    public string? DedupeKey { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
}

public sealed class EmailPreference
{
    public Guid UserId { get; set; }
    public string Type { get; set; } = EmailPreferenceTypes.RepliesMentions;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EmailSuppression
{
    public byte[] EmailHash { get; set; } = Array.Empty<byte>();
    public string Reason { get; set; } = EmailSuppressionReasons.Manual;
    public string? Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}