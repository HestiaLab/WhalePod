using Microsoft.EntityFrameworkCore;

using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record DatabaseReadinessResult(bool Ready, string? Reason);

public static class DatabaseReadiness
{
    public static async Task<DatabaseReadinessResult> CheckAsync(
        WhalePodDbContext db,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return new DatabaseReadinessResult(false, "database_unavailable");
            }

            var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pendingMigrations.Any())
            {
                return new DatabaseReadinessResult(false, "migrations_pending");
            }

            await db.SiteSettings.AsNoTracking().AnyAsync(cancellationToken);
            return new DatabaseReadinessResult(true, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DatabaseReadinessResult(false, "probe_failed");
        }
    }
}