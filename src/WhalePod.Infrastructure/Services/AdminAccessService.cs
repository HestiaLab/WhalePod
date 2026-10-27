using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class AdminAccessService(WhalePodDbContext db)
{
    public Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return db.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.Id == userId
                    && user.Role == UserRoles.Admin
                    && user.Status == UserStatuses.Active
                    && user.ActivatedAt != null
                    && user.DeletedAt == null,
                cancellationToken);
    }
}