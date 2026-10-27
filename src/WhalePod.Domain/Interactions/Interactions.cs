using System.Text.Json;

using WhalePod.Domain;

namespace WhalePod.Domain.Interactions;

public sealed class PostLike
{
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CommentLike
{
    public Guid CommentId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Report
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ReporterId { get; set; }
    public string TargetType { get; set; } = ReportTargetTypes.Post;
    public Guid TargetId { get; set; }
    public string Reason { get; set; } = ReportReasons.Other;
    public string? Detail { get; set; }
    public string Status { get; set; } = ReportStatuses.Pending;
    public Guid? HandledBy { get; set; }
    public DateTimeOffset? HandledAt { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Type { get; set; } = NotificationTypes.Reply;
    public JsonDocument PayloadJson { get; set; } = JsonDocument.Parse("{}");
    public string? DedupeKey { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset LastOccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}