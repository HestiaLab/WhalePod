using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record AdminUserView(
    Guid Id,
    string Handle,
    string DisplayName,
    string Status,
    string? StatusReason,
    DateTimeOffset? StatusExpiresAt,
    short TrustLevel,
    string Role,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record AdminUserPage(
    IReadOnlyList<AdminUserView> Items,
    string? NextCursor,
    bool InvalidCursor);

public sealed class AdminUserService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    EmailNotificationQueue emailQueue,
    TimeProvider timeProvider)
{
    public async Task<AdminUserPage> ListAsync(
        Guid actorId,
        string? query,
        string? status,
        int? trustLevel,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (normalizedQuery is not null && RuneHelper.Count(normalizedQuery) > 80)
        {
            throw ApiProblemException.Validation("query", "查询关键词不能超过 80 个字符。");
        }

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not null
            && normalizedStatus is not (UserStatuses.Active
                or UserStatuses.Muted
                or UserStatuses.Banned
                or UserStatuses.Deleted))
        {
            throw ApiProblemException.Validation("status", "用户状态筛选无效。");
        }

        if (trustLevel is < 0 or > 4)
        {
            throw ApiProblemException.Validation("trustLevel", "信任等级必须在 0–4 之间。");
        }

        var scope = AdminCursor.BuildScope(
            "admin-users",
            normalizedQuery,
            normalizedStatus,
            trustLevel?.ToString());
        DateTimeOffset? cursorTime = null;
        Guid cursorId = default;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!AdminCursor.TryDecode(cursor, scope, out var decodedTime, out var decodedId))
            {
                return new AdminUserPage([], null, true);
            }

            cursorTime = decodedTime;
            cursorId = decodedId;
        }

        var users = db.Users.AsNoTracking();
        if (normalizedQuery is not null)
        {
            var pattern = $"%{EscapeLike(normalizedQuery)}%";
            users = users.Where(user =>
                EF.Functions.ILike(user.Handle, pattern, "\\")
                || EF.Functions.ILike(user.DisplayName, pattern, "\\"));
        }

        if (normalizedStatus is not null)
        {
            users = users.Where(user => user.Status == normalizedStatus);
        }

        if (trustLevel is not null)
        {
            users = users.Where(user => user.TrustLevel == trustLevel.Value);
        }

        if (cursorTime is not null)
        {
            users = users.Where(user =>
                user.CreatedAt < cursorTime
                || (user.CreatedAt == cursorTime && user.Id.CompareTo(cursorId) < 0));
        }

        var rows = await users
            .OrderByDescending(user => user.CreatedAt)
            .ThenByDescending(user => user.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        var pageRows = hasMore ? rows.Take(limit).ToList() : rows;
        var nextCursor = hasMore
            ? AdminCursor.Encode(scope, pageRows[^1].CreatedAt, pageRows[^1].Id)
            : null;
        return new AdminUserPage(pageRows.Select(ToView).ToList(), nextCursor, false);
    }

    public async Task<AdminUserView> GetAsync(
        Guid actorId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        return user is null
            ? throw ApiProblemException.NotFound("用户不存在。")
            : ToView(user);
    }

    public async Task<AdminUserView> ChangeStatusAsync(
        Guid actorId,
        Guid targetUserId,
        string? status,
        string? reason,
        DateTimeOffset? expiresAt,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        var normalizedStatus = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedStatus is not (UserStatuses.Active or UserStatuses.Muted or UserStatuses.Banned))
        {
            throw ApiProblemException.Validation("status", "状态必须是 active、muted 或 banned。");
        }

        var normalizedReason = NormalizeReason(reason);
        var normalizedExpiry = expiresAt?.ToUniversalTime();
        var now = timeProvider.GetUtcNow();
        if (normalizedStatus == UserStatuses.Muted)
        {
            if (normalizedExpiry is null)
            {
                throw ApiProblemException.Validation("expiresAt", "禁言必须填写到期时间。");
            }

            if (normalizedExpiry <= now)
            {
                throw ApiProblemException.Validation("expiresAt", "禁言到期时间必须晚于当前时间。");
            }
        }
        else if (normalizedStatus == UserStatuses.Banned)
        {
            if (normalizedExpiry is not null && normalizedExpiry <= now)
            {
                throw ApiProblemException.Validation("expiresAt", "封禁到期时间必须晚于当前时间。");
            }
        }
        else if (normalizedExpiry is not null)
        {
            throw ApiProblemException.Validation("expiresAt", "解除处罚时不能设置到期时间。");
        }

        if (actorId == targetUserId)
        {
            throw ApiProblemException.Forbidden("self_operation", "不能修改自己的账号状态。");
        }

        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7210521)", cancellationToken);

            var target = await db.Users
                .SingleOrDefaultAsync(candidate => candidate.Id == targetUserId, cancellationToken);
            if (target is null)
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            if (target.Status == UserStatuses.Deleted || target.DeletedAt is not null)
            {
                throw ApiProblemException.Conflict("已注销账号不能变更状态。");
            }

            if (!IsAllowedTransition(target.Status, normalizedStatus))
            {
                throw ApiProblemException.Conflict("当前账号状态不允许该流转。", new
                {
                    currentStatus = target.Status,
                    requestedStatus = normalizedStatus
                });
            }

            if (target.Role == UserRoles.Admin
                && target.Status == UserStatuses.Active
                && normalizedStatus is UserStatuses.Muted or UserStatuses.Banned)
            {
                var otherAdmins = await db.Users.CountAsync(user =>
                    user.Id != target.Id
                    && user.Role == UserRoles.Admin
                    && user.Status == UserStatuses.Active
                    && user.ActivatedAt != null
                    && user.DeletedAt == null, cancellationToken);
                if (otherAdmins == 0)
                {
                    throw ApiProblemException.Forbidden("last_admin", "不能处罚最后一个有效管理员。");
                }
            }

            var previousStatus = target.Status;
            target.Status = normalizedStatus;
            target.StatusReason = normalizedStatus == UserStatuses.Active ? null : normalizedReason;
            target.StatusExpiresAt = normalizedStatus == UserStatuses.Active ? null : normalizedExpiry;
            target.UpdatedAt = now;

            if (normalizedStatus is UserStatuses.Muted or UserStatuses.Banned)
            {
                db.ModerationActions.Add(new ModerationAction
                {
                    TargetType = "user",
                    TargetId = target.Id,
                    ActorType = ModerationActorTypes.User,
                    ActorId = actorId,
                    Action = normalizedStatus == UserStatuses.Muted
                        ? ModerationActions.Mute
                        : ModerationActions.Ban,
                    FromStatus = previousStatus,
                    ToStatus = normalizedStatus,
                    Reason = normalizedReason,
                    ContentVersion = 1,
                    CreatedAt = now
                });
            }

            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                "user.status_changed",
                "user",
                target.Id,
                new
                {
                    previousStatus,
                    status = normalizedStatus,
                    reason = normalizedReason,
                    expiresAt = normalizedExpiry
                },
                now));

            var notificationPayload = new
            {
                status = normalizedStatus,
                reason = normalizedReason,
                expiresAt = normalizedExpiry
            };
            var dedupeKey = $"account-status:{target.Id}:{normalizedStatus}:{now.UtcDateTime.Ticks}";
            db.Notifications.Add(NotificationWriter.Create(
                target.Id,
                NotificationTypes.AccountStatusChanged,
                notificationPayload,
                dedupeKey,
                now));
            await emailQueue.EnqueueForNotificationAsync(
                target.Id,
                NotificationTypes.AccountStatusChanged,
                notificationPayload,
                dedupeKey,
                now,
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            var view = ToView(target);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return view;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task<AdminUserView> ChangeTrustLevelAsync(
        Guid actorId,
        Guid targetUserId,
        short trustLevel,
        string? reason,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        if (trustLevel is < 0 or > 4)
        {
            throw ApiProblemException.Validation("trustLevel", "信任等级必须在 0–4 之间。");
        }

        var normalizedReason = NormalizeReason(reason);
        var now = timeProvider.GetUtcNow();

        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var target = await db.Users
                .SingleOrDefaultAsync(candidate => candidate.Id == targetUserId, cancellationToken);
            if (target is null)
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            if (target.Status is UserStatuses.Banned or UserStatuses.Deleted || target.DeletedAt is not null)
            {
                throw ApiProblemException.Conflict("当前账号状态不允许调整信任等级。");
            }

            if (actorId == targetUserId && trustLevel < target.TrustLevel)
            {
                throw ApiProblemException.Forbidden("self_demote", "不能降级自己的信任等级。");
            }

            if (target.TrustLevel == trustLevel)
            {
                var unchanged = ToView(target);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return unchanged;
            }

            var previousLevel = target.TrustLevel;
            target.TrustLevel = trustLevel;
            target.UpdatedAt = now;

            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                "user.trust_level_changed",
                "user",
                target.Id,
                new
                {
                    previousTrustLevel = previousLevel,
                    trustLevel,
                    reason = normalizedReason
                },
                now));

            db.Notifications.Add(NotificationWriter.Create(
                target.Id,
                NotificationTypes.AccountStatusChanged,
                new
                {
                    trustLevel,
                    reason = normalizedReason
                },
                $"trust-level:{target.Id}:{trustLevel}:{now.UtcDateTime.Ticks}",
                now));

            await db.SaveChangesAsync(cancellationToken);
            var view = ToView(target);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return view;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static bool IsAllowedTransition(string current, string requested)
    {
        return (current, requested) switch
        {
            (UserStatuses.Active, UserStatuses.Muted) => true,
            (UserStatuses.Active, UserStatuses.Banned) => true,
            (UserStatuses.Muted, UserStatuses.Active) => true,
            (UserStatuses.Muted, UserStatuses.Banned) => true,
            (UserStatuses.Banned, UserStatuses.Active) => true,
            _ => false
        };
    }

    private static string NormalizeReason(string? reason)
    {
        var normalized = (reason ?? string.Empty).Trim();
        if (RuneHelper.Count(normalized) is < 1 or > 500)
        {
            throw ApiProblemException.Validation("reason", "必须填写 1–500 个字符的原因。");
        }

        return normalized;
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static AdminUserView ToView(User user)
    {
        return new AdminUserView(
            user.Id,
            user.Handle,
            user.DisplayName,
            user.Status,
            user.StatusReason,
            user.StatusExpiresAt,
            user.TrustLevel,
            user.Role,
            user.ActivatedAt,
            user.CreatedAt,
            user.LastLoginAt);
    }
}