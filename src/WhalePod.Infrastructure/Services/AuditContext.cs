namespace WhalePod.Infrastructure.Services;

public sealed record AuditContext(Guid ActorId, string? RequestId = null, byte[]? IpHash = null);