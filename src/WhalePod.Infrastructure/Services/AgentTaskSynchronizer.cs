using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using WhalePod.Domain;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

internal sealed class AgentTaskSynchronizer(WhalePodDbContext db, ILogger logger)
{
    public async Task SynchronizeAsync(
        DateTimeOffset now,
        int contentBudget,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(7210523)",
            cancellationToken);
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var createdToday = await db.ModerationTasks
            .AsNoTracking()
            .CountAsync(
                task => task.CreatedAt >= dayStart && task.CreatedAt < dayStart.AddDays(1),
                cancellationToken);
        var remaining = Math.Max(0, contentBudget - createdToday);
        if (remaining == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var pendingPosts = await db.Posts
            .AsNoTracking()
            .Where(post => post.ModerationStatus == ModerationStatuses.Pending
                && post.DeletedAt == null
                && !db.ModerationTasks.Any(task =>
                    task.TargetType == ModerationTargetTypes.Post
                    && task.TargetId == post.Id
                    && task.ContentVersion == post.Version
                    && AgentModerationService.ActiveTaskStatuses.Contains(task.Status)))
            .OrderBy(post => post.CreatedAt)
            .Take(50)
            .Select(post => new
            {
                post.Id,
                post.Version,
                post.Title,
                post.BodyMd,
                post.CategoryId,
                post.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var pendingComments = await db.Comments
            .AsNoTracking()
            .Where(comment => comment.ModerationStatus == ModerationStatuses.Pending
                && comment.DeletedAt == null
                && !db.ModerationTasks.Any(task =>
                    task.TargetType == ModerationTargetTypes.Comment
                    && task.TargetId == comment.Id
                    && task.ContentVersion == comment.Version
                    && AgentModerationService.ActiveTaskStatuses.Contains(task.Status)))
            .OrderBy(comment => comment.CreatedAt)
            .Take(50)
            .Select(comment => new
            {
                comment.Id,
                comment.Version,
                comment.PostId,
                comment.ParentId,
                comment.BodyMd,
                comment.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var postIds = pendingPosts.Select(post => post.Id).ToList();
        var commentIds = pendingComments.Select(comment => comment.Id).ToList();
        var tasks = await db.ModerationTasks
            .Where(task => (task.TargetType == ModerationTargetTypes.Post && postIds.Contains(task.TargetId))
                || (task.TargetType == ModerationTargetTypes.Comment && commentIds.Contains(task.TargetId)))
            .Where(task => AgentModerationService.ActiveTaskStatuses.Contains(task.Status))
            .ToListAsync(cancellationToken);

        var created = false;
        var createdCount = 0;
        foreach (var post in pendingPosts)
        {
            if (createdCount >= remaining)
            {
                break;
            }

            var hash = AgentContentHash.ForPost(post.Id, post.Version, post.Title, post.BodyMd, post.CategoryId);
            var active = tasks
                .Where(task => task.TargetType == ModerationTargetTypes.Post && task.TargetId == post.Id)
                .ToList();
            var current = active.FirstOrDefault(task =>
                task.ContentVersion == post.Version && task.ContentHash == hash);
            if (current is not null)
            {
                continue;
            }

            foreach (var stale in active)
            {
                stale.Status = ModerationTaskStatuses.Expired;
                stale.CompletedAt = now;
                stale.LeaseExpiresAt = null;
                stale.LeaseToken = null;
                stale.LastErrorCode = "content_stale";
            }

            var riskHints = await LoadRiskHintsAsync(
                ModerationTargetTypes.Post,
                post.Id,
                post.Version,
                cancellationToken);
            var reportCount = await db.Reports
                .AsNoTracking()
                .CountAsync(report => report.TargetType == ModerationTargetTypes.Post
                    && report.TargetId == post.Id
                    && report.Status == ReportStatuses.Pending, cancellationToken);
            db.ModerationTasks.Add(NewTask(
                ModerationTargetTypes.Post,
                post.Id,
                post.Version,
                hash,
                riskHints,
                reportCount,
                now));
            created = true;
            createdCount++;
        }

        foreach (var comment in pendingComments)
        {
            if (createdCount >= remaining)
            {
                break;
            }

            var hash = AgentContentHash.ForComment(
                comment.Id,
                comment.Version,
                comment.PostId,
                comment.ParentId,
                comment.BodyMd);
            var active = tasks
                .Where(task => task.TargetType == ModerationTargetTypes.Comment && task.TargetId == comment.Id)
                .ToList();
            var current = active.FirstOrDefault(task =>
                task.ContentVersion == comment.Version && task.ContentHash == hash);
            if (current is not null)
            {
                continue;
            }

            foreach (var stale in active)
            {
                stale.Status = ModerationTaskStatuses.Expired;
                stale.CompletedAt = now;
                stale.LeaseExpiresAt = null;
                stale.LeaseToken = null;
                stale.LastErrorCode = "content_stale";
            }

            var riskHints = await LoadRiskHintsAsync(
                ModerationTargetTypes.Comment,
                comment.Id,
                comment.Version,
                cancellationToken);
            var reportCount = await db.Reports
                .AsNoTracking()
                .CountAsync(report => report.TargetType == ModerationTargetTypes.Comment
                    && report.TargetId == comment.Id
                    && report.Status == ReportStatuses.Pending, cancellationToken);
            db.ModerationTasks.Add(NewTask(
                ModerationTargetTypes.Comment,
                comment.Id,
                comment.Version,
                hash,
                riskHints,
                reportCount,
                now));
            created = true;
            createdCount++;
        }

        if (created || db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                logger.LogInformation("Concurrent Agent task synchronization hit a unique task; continuing.");
                return;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadRiskHintsAsync(
        string targetType,
        Guid targetId,
        int contentVersion,
        CancellationToken cancellationToken)
    {
        var document = await db.ModerationChecks
            .AsNoTracking()
            .Where(check => check.TargetType == targetType
                && check.TargetId == targetId
                && check.ContentVersion == contentVersion)
            .OrderByDescending(check => check.CreatedAt)
            .ThenByDescending(check => check.Id)
            .Select(check => check.RiskLabels)
            .FirstOrDefaultAsync(cancellationToken);
        return document is null ? [] : AgentContentProjector.ReadRiskHints(document);
    }

    private static ModerationTask NewTask(
        string targetType,
        Guid targetId,
        int contentVersion,
        string contentHash,
        IReadOnlyList<string> riskHints,
        int reportCount,
        DateTimeOffset now)
    {
        var priority = riskHints.Count > 0 ? 80 : reportCount > 0 ? 70 : 40;
        return new ModerationTask
        {
            TargetType = targetType,
            TargetId = targetId,
            ContentVersion = contentVersion,
            ContentHash = contentHash,
            Status = ModerationTaskStatuses.Queued,
            Priority = (short)priority,
            RiskHintsJson = JsonSerializer.SerializeToDocument(riskHints),
            CreatedAt = now,
            TaskExpiresAt = now.AddDays(7)
        };
    }
}