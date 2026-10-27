using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WhalePod.Infrastructure.Persistence;

public sealed class WhalePodDbContextFactory : IDesignTimeDbContextFactory<WhalePodDbContext>
{
    public WhalePodDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("WHALEPOD_DB_CONNECTION")
            ?? "Host=localhost;Database=whalepod;Username=whalepod";

        var options = new DbContextOptionsBuilder<WhalePodDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(WhalePodDbContext).Assembly.GetName().Name))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WhalePodDbContext(options);
    }
}