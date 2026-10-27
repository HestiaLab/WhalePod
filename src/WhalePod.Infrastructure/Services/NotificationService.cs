using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record NotificationPayloadView(
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetType,
    Guid? TargetId,
    string? TargetUrl);

public sealed record NotificationView(
    Guid Id,
    string Type,
    NotificationPayloadView Payload,
    DateTimeOffset? ReadAt,
    DateTimeOffset CreatedAt);

public sealed record NotificationListPage(
    IReadOnlyList<NotificationView> Items,
    string? NextCursor,
    int UnreadCount,
    bool InvalidCursor);

public sealed record NotificationReadResultView(bool Read, int UnreadCount);

public sealed class NotificationService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    TimeProvider timeProvider)
{
    private static readonly string[] Filters = ["all", "unread"];

    public async Task<NotificationListPage> ListAsync(
        Guid actorId,
        string? filter,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var normalizedFilter = string.IsNullOrWhiteSpace(filter)
            ? "all"
            : filter.Trim().ToLowerInvariant();
        if (!Filters.Contains(normalizedFilter, StringComparer.Ordinal))
        {
            throw ApiProblemException.Validation("filter", "filter 必须是 all 或 unread。");
        }

        var scope = AdminCursor.BuildScope("notifications", actorId.ToString("N"), normalizedFilter);
        DateTimeOffset? cursorTime = null;
        var cursorId = default(Guid);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!AdminCursor.TryDecode(cursor, scope, out var decodedTime, out var decodedId))
            {
                return new NotificationListPage([], null, 0, true);
            }

            cursorTime = decodedTime;
            cursorId = decodedId;
        }

        var query = db.Notifications
            .AsNoTracking()
            .Where(notification => notification.UserId == actorId);
        if (normalizedFilter == "unread")
        {
            query = query.Where(notification => notification.ReadAt == null);
        }

        if (cursorTime is not null)
        {
            query = query.Where(notification =>
                notification.CreatedAt < cursorTime
                || (notification.CreatedAt == cursorTime
                    && notification.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var unreadCount = await db.Notifications
            .AsNoTracking()
            .CountAsync(
                notification => notification.UserId == actorId && notification.ReadAt == null,
                cancellationToken);

        var hasMore = rows.Count > limit;
        var pageRows = hasMore ? rows.Take(limit).ToList() : rows;
        var items = pageRows
            .Select(notification => new NotificationView(
                notification.Id,
                notification.Type,
                NotificationPresentation.Map(notification),
                notification.ReadAt,
                notification.CreatedAt))
            .ToList();
        var nextCursor = hasMore
            ? AdminCursor.Encode(scope, pageRows[^1].CreatedAt, pageRows[^1].Id)
            : null;
        return new NotificationListPage(items, nextCursor, unreadCount, false);
    }

    public async Task<NotificationReadResultView> MarkReadAsync(
        Guid actorId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var notification = await db.Notifications
            .SingleOrDefaultAsync(
                candidate => candidate.Id == notificationId && candidate.UserId == actorId,
                cancellationToken);
        if (notification is null)
        {
            throw ApiProblemException.NotFound("通知不存在。");
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        var unreadCount = await CountUnreadAsync(actorId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NotificationReadResultView(true, unreadCount);
    }

    public async Task<NotificationReadResultView> MarkAllReadAsync(
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Notifications
            .Where(notification => notification.UserId == actorId && notification.ReadAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(notification => notification.ReadAt, now),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NotificationReadResultView(true, 0);
    }

    private Task<int> CountUnreadAsync(Guid actorId, CancellationToken cancellationToken)
    {
        return db.Notifications
            .AsNoTracking()
            .CountAsync(
                notification => notification.UserId == actorId && notification.ReadAt == null,
                cancellationToken);
    }

}