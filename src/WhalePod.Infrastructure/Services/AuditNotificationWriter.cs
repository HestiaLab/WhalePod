using System.Text.Json;

using WhalePod.Domain;
using WhalePod.Domain.Interactions;
using WhalePod.Domain.Platform;

namespace WhalePod.Infrastructure.Services;

internal static class AuditLogWriter
{
    public static AuditLog CreateUserAction(
        AuditContext audit,
        string action,
        string? targetType,
        Guid? targetId,
        object? metadata,
        DateTimeOffset now)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = audit.ActorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(metadata ?? new { }),
            CreatedAt = now
        };
    }

    public static AuditLog CreateServiceAction(
        AuditContext audit,
        string action,
        string? targetType,
        Guid? targetId,
        object? metadata,
        DateTimeOffset now)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.Service,
            ActorId = audit.ActorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(metadata ?? new { }),
            CreatedAt = now
        };
    }

    public static AuditLog CreateSystemAction(
        string action,
        string? targetType,
        Guid? targetId,
        object? metadata,
        DateTimeOffset now)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.System,
            ActorId = null,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            RequestId = null,
            IpHash = null,
            MetadataJson = JsonSerializer.SerializeToDocument(metadata ?? new { }),
            CreatedAt = now
        };
    }
}

internal static class NotificationWriter
{
    public static Notification Create(
        Guid userId,
        string type,
        object payload,
        string dedupeKey,
        DateTimeOffset now)
    {
        return new Notification
        {
            UserId = userId,
            Type = type,
            PayloadJson = JsonSerializer.SerializeToDocument(payload),
            DedupeKey = dedupeKey,
            OccurrenceCount = 1,
            LastOccurredAt = now,
            CreatedAt = now
        };
    }
}