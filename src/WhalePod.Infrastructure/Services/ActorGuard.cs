using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum ActorWriteScope
{
    Content,
    Report
}

public sealed class ActorGuard(WhalePodDbContext db, TimeProvider timeProvider)
{
    public async Task<User> RequireWriteAccessAsync(
        Guid userId,
        ActorWriteScope scope,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null)
        {
            throw new ApiProblemException(
                ApiErrorCodes.AuthRequired,
                401,
                "请先登录后再进行此操作。");
        }

        if (user.DeletedAt is not null || user.Status == UserStatuses.Deleted)
        {
            throw ApiProblemException.Forbidden("account_deleted", "账号已注销，不能执行此操作。");
        }

        if (user.ActivatedAt is null)
        {
            throw ApiProblemException.Forbidden("activation_required", "账号尚未激活，不能执行此操作。");
        }

        if (user.Status == UserStatuses.Banned)
        {
            throw new ApiProblemException(
                ApiErrorCodes.AccountBanned,
                403,
                "账号已被封禁，不能执行此操作。",
                new { statusExpiresAt = user.StatusExpiresAt });
        }

        if (scope == ActorWriteScope.Content && IsMuted(user, timeProvider.GetUtcNow()))
        {
            throw new ApiProblemException(
                ApiErrorCodes.AccountMuted,
                403,
                "账号处于禁言状态，不能执行该写操作。",
                new { expiresAt = user.StatusExpiresAt });
        }

        return user;
    }

    public async Task<User> RequireReadAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null)
        {
            throw new ApiProblemException(
                ApiErrorCodes.AuthRequired,
                401,
                "请先登录后再进行此操作。");
        }

        if (user.DeletedAt is not null || user.Status == UserStatuses.Deleted)
        {
            throw ApiProblemException.Forbidden("account_deleted", "账号已注销，不能执行此操作。");
        }

        if (user.ActivatedAt is null)
        {
            throw ApiProblemException.Forbidden("activation_required", "账号尚未激活，不能执行此操作。");
        }

        if (user.Status == UserStatuses.Banned)
        {
            throw new ApiProblemException(
                ApiErrorCodes.AccountBanned,
                403,
                "账号已被封禁，不能执行此操作。",
                new { statusExpiresAt = user.StatusExpiresAt });
        }

        return user;
    }

    public async Task<User> RequireAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null)
        {
            throw new ApiProblemException(
                ApiErrorCodes.AuthRequired,
                401,
                "请先登录后再进行此操作。");
        }

        if (user.Role != UserRoles.Admin
            || user.Status != UserStatuses.Active
            || user.ActivatedAt is null
            || user.DeletedAt is not null)
        {
            throw ApiProblemException.Forbidden();
        }

        return user;
    }

    public static bool IsMuted(User user, DateTimeOffset now)
    {
        return user.Status == UserStatuses.Muted
            && (user.StatusExpiresAt is null || user.StatusExpiresAt > now);
    }
}