using System.Text.Json;

using Microsoft.Extensions.Logging;

using WhalePod.Domain;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class AgentFailureRecorder(
    WhalePodDbContext db,
    TimeProvider timeProvider,
    ILogger<AgentFailureRecorder> logger)
{
    public async Task RecordSuggestionInvalidAsync(
        Guid serviceAccountId,
        Guid taskId,
        string code,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.Service,
            ActorId = serviceAccountId,
            Action = AiBudgetService.SuggestionFailedAction,
            TargetType = "moderation_task",
            TargetId = taskId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                code,
                count = 1
            }),
            CreatedAt = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Failed to record agent suggestion failure for task {TaskId}.",
                taskId);
            db.ChangeTracker.Clear();
        }
    }
}