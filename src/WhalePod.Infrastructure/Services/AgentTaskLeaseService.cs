using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record AgentTaskLease(Guid Token, DateTimeOffset ExpiresAt);

public sealed class AgentTaskLeaseService(WhalePodDbContext db)
{
    public async Task<AgentTaskLease?> TryClaimAsync(
        Guid taskId,
        Guid instanceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var leaseToken = Guid.NewGuid();
        var leaseExpiresAt = now.AddSeconds(AgentPolicyDefaults.LeaseSeconds);
        var updated = await db.ModerationTasks
            .Where(candidate => candidate.Id == taskId
                && (candidate.Status == ModerationTaskStatuses.Queued
                    || (candidate.Status == ModerationTaskStatuses.Claimed
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt <= now)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, ModerationTaskStatuses.Claimed)
                    .SetProperty(candidate => candidate.ClaimedByAgentInstanceId, instanceId)
                    .SetProperty(candidate => candidate.LeaseToken, leaseToken)
                    .SetProperty(candidate => candidate.ClaimedAt, now)
                    .SetProperty(candidate => candidate.LeaseExpiresAt, leaseExpiresAt)
                    .SetProperty(candidate => candidate.LastErrorCode, (string?)null),
                cancellationToken);
        return updated == 0 ? null : new AgentTaskLease(leaseToken, leaseExpiresAt);
    }

    public async Task<bool> TryMarkSuggestedAsync(
        Guid taskId,
        Guid instanceId,
        Guid? leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var updated = await db.ModerationTasks
            .Where(candidate => candidate.Id == taskId
                && candidate.Status == ModerationTaskStatuses.Claimed
                && candidate.ClaimedByAgentInstanceId == instanceId
                && candidate.LeaseExpiresAt != null
                && candidate.LeaseExpiresAt > now
                && candidate.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, ModerationTaskStatuses.Suggested)
                    .SetProperty(candidate => candidate.SuggestedAt, now)
                    .SetProperty(candidate => candidate.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.LeaseToken, (Guid?)null),
                cancellationToken);
        return updated > 0;
    }

    public async Task<bool> TryReleaseAsync(
        Guid taskId,
        Guid instanceId,
        Guid? leaseToken,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var updated = await db.ModerationTasks
            .Where(candidate => candidate.Id == taskId
                && candidate.Status == ModerationTaskStatuses.Claimed
                && candidate.ClaimedByAgentInstanceId == instanceId
                && candidate.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, ModerationTaskStatuses.Queued)
                    .SetProperty(candidate => candidate.ClaimedByAgentInstanceId, (Guid?)null)
                    .SetProperty(candidate => candidate.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                    .SetProperty(candidate => candidate.LastErrorCode, reason),
                cancellationToken);
        return updated > 0;
    }

    public async Task<int> ReleaseExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var hasExpired = await db.ModerationTasks
            .AsNoTracking()
            .AnyAsync(
                task => task.Status == ModerationTaskStatuses.Claimed
                    && task.LeaseExpiresAt != null
                    && task.LeaseExpiresAt <= now,
                cancellationToken);
        if (!hasExpired)
        {
            return 0;
        }

        return await db.ModerationTasks
            .Where(task => task.Status == ModerationTaskStatuses.Claimed
                && task.LeaseExpiresAt != null
                && task.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.Status, ModerationTaskStatuses.Queued)
                    .SetProperty(task => task.ClaimedByAgentInstanceId, (Guid?)null)
                    .SetProperty(task => task.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseToken, (Guid?)null)
                    .SetProperty(task => task.LastErrorCode, "lease_expired"),
                cancellationToken);
    }

    public Task<int> ReleaseLeaseAsync(
        Guid taskId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return db.ModerationTasks
            .Where(task => task.Id == taskId
                && task.Status == ModerationTaskStatuses.Claimed
                && task.LeaseExpiresAt != null
                && task.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.Status, ModerationTaskStatuses.Queued)
                    .SetProperty(task => task.ClaimedByAgentInstanceId, (Guid?)null)
                    .SetProperty(task => task.ClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseToken, (Guid?)null)
                    .SetProperty(task => task.LastErrorCode, reason),
                cancellationToken);
    }

    public Task ExpireAsync(
        Guid taskId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return db.ModerationTasks
            .Where(task => task.Id == taskId && AgentModerationService.ActiveTaskStatuses.Contains(task.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(task => task.Status, ModerationTaskStatuses.Expired)
                    .SetProperty(task => task.CompletedAt, now)
                    .SetProperty(task => task.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(task => task.LeaseToken, (Guid?)null)
                    .SetProperty(task => task.LastErrorCode, reason),
                cancellationToken);
    }
}