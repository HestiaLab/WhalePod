using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WhalePod.Infrastructure.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWhalePodDbContext(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<WhalePodDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsAssembly(typeof(WhalePodDbContext).Assembly.GetName().Name))
                .UseSnakeCaseNamingConvention());

        return services;
    }
}