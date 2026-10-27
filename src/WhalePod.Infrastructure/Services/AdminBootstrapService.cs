using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class AdminBootstrapService(
    WhalePodDbContext db,
    IOptions<AdminBootstrapOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminBootstrapService> logger)
{
    public async Task<bool> EnsureAdminAsync(User user, AuditContext audit, CancellationToken cancellationToken = default)
    {
        if (user.Role == UserRoles.Admin
            || user.Status != UserStatuses.Active
            || user.DeletedAt is not null)
        {
            return false;
        }

        var handles = options.Value.BootstrapAdminHandles ?? [];
        if (!handles.Any(handle => string.Equals(handle.Trim(), user.Handle, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        user.Role = UserRoles.Admin;
        user.UpdatedAt = now;

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = user.Id,
            Action = "admin.bootstrap_granted",
            TargetType = "user",
            TargetId = user.Id,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new { handle = user.Handle }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Bootstrap admin role granted to user {UserId}.", user.Id);
        return true;
    }
}