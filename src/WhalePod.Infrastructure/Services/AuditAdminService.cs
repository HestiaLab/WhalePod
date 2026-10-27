using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record AuditFilter(
    string? ActorHandle,
    string? ActionPrefix,
    string? TargetType,
    Guid? TargetId,
    DateTimeOffset From,
    DateTimeOffset ToExclusive);

public sealed record AuditListItem(
    Guid Id,
    DateTimeOffset CreatedAt,
    string ActorType,
    Guid? ActorId,
    string? ActorHandle,
    string Action,
    string? TargetType,
    Guid? TargetId,
    string? RequestId);

public sealed record AuditListResult(
    IReadOnlyList<AuditListItem> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record AuditDetailResult(AuditListItem Item, string MetadataJson);

public sealed record AuditExportRow(
    DateTimeOffset CreatedAt,
    string ActorType,
    Guid? ActorId,
    string? ActorHandle,
    string Action,
    string? TargetType,
    Guid? TargetId,
    string? RequestId,
    string MetadataJson);

public sealed class AuditAdminService(WhalePodDbContext db, TimeProvider timeProvider)
{
    public const int PageSize = 20;
    public const int MaxExportDays = 31;

    public async Task<AuditListResult> ListAsync(
        AuditFilter filter,
        int page,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        var query = BuildQuery(filter);
        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await (
                from log in query
                join actor in db.Users.AsNoTracking() on log.ActorId equals (Guid?)actor.Id into actors
                from actor in actors.DefaultIfEmpty()
                orderby log.CreatedAt descending, log.Id descending
                select new
                {
                    log.Id,
                    log.CreatedAt,
                    log.ActorType,
                    log.ActorId,
                    ActorHandle = actor != null ? actor.Handle : null,
                    log.Action,
                    log.TargetType,
                    log.TargetId,
                    log.RequestId
                })
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new AuditListItem(
                row.Id,
                row.CreatedAt,
                row.ActorType,
                row.ActorId,
                row.ActorHandle,
                row.Action,
                row.TargetType,
                row.TargetId,
                row.RequestId))
            .ToList();

        return new AuditListResult(items, totalCount, page, PageSize);
    }

    public async Task<AuditDetailResult?> GetDetailAsync(
        Guid auditLogId,
        CancellationToken cancellationToken = default)
    {
        var row = await (
                from log in db.AuditLogs.AsNoTracking()
                join actor in db.Users.AsNoTracking() on log.ActorId equals (Guid?)actor.Id into actors
                from actor in actors.DefaultIfEmpty()
                where log.Id == auditLogId
                select new
                {
                    log.Id,
                    log.CreatedAt,
                    log.ActorType,
                    log.ActorId,
                    ActorHandle = actor != null ? actor.Handle : null,
                    log.Action,
                    log.TargetType,
                    log.TargetId,
                    log.RequestId,
                    log.MetadataJson
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new AuditDetailResult(
            new AuditListItem(
                row.Id,
                row.CreatedAt,
                row.ActorType,
                row.ActorId,
                row.ActorHandle,
                row.Action,
                row.TargetType,
                row.TargetId,
                row.RequestId),
            AuditMetadataRedactor.Redact(row.MetadataJson));
    }

    public async IAsyncEnumerable<AuditExportRow> ExportAsync(
        AuditFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query =
            from log in BuildQuery(filter)
            join actor in db.Users.AsNoTracking() on log.ActorId equals (Guid?)actor.Id into actors
            from actor in actors.DefaultIfEmpty()
            orderby log.CreatedAt, log.Id
            select new
            {
                log.CreatedAt,
                log.ActorType,
                log.ActorId,
                ActorHandle = actor != null ? actor.Handle : null,
                log.Action,
                log.TargetType,
                log.TargetId,
                log.RequestId,
                log.MetadataJson
            };

        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return new AuditExportRow(
                row.CreatedAt,
                row.ActorType,
                row.ActorId,
                row.ActorHandle,
                row.Action,
                row.TargetType,
                row.TargetId,
                row.RequestId,
                AuditMetadataRedactor.Redact(row.MetadataJson));
        }
    }

    public async Task RecordExportAsync(
        Guid actorId,
        AuditFilter filter,
        int rowCount,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = actorId,
            Action = "audit.exported",
            TargetType = "audit_log",
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                from = filter.From,
                to = filter.ToExclusive,
                rows = rowCount,
                actorHandle = filter.ActorHandle,
                actionPrefix = filter.ActionPrefix,
                targetType = filter.TargetType,
                targetId = filter.TargetId
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public static bool IsExportRangeValid(AuditFilter filter)
    {
        return filter.ToExclusive > filter.From
            && filter.ToExclusive - filter.From <= TimeSpan.FromDays(MaxExportDays);
    }

    private IQueryable<AuditLog> BuildQuery(AuditFilter filter)
    {
        var query = db.AuditLogs
            .AsNoTracking()
            .Where(log => log.CreatedAt >= filter.From && log.CreatedAt < filter.ToExclusive);

        if (!string.IsNullOrWhiteSpace(filter.ActorHandle))
        {
            var handle = filter.ActorHandle.Trim().ToLowerInvariant();
            var actorIds = db.Users
                .AsNoTracking()
                .Where(user => user.Handle == handle)
                .Select(user => (Guid?)user.Id);

            query = query.Where(log =>
                log.ActorId != null && actorIds.Contains(log.ActorId.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.ActionPrefix))
        {
            var actionPrefix = filter.ActionPrefix.Trim();
            query = query.Where(log => log.Action.StartsWith(actionPrefix));
        }

        if (!string.IsNullOrWhiteSpace(filter.TargetType))
        {
            var targetType = filter.TargetType.Trim();
            query = query.Where(log => log.TargetType == targetType);
        }

        if (filter.TargetId is not null)
        {
            query = query.Where(log => log.TargetId == filter.TargetId);
        }

        return query;
    }
}