using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Infrastructure.DependencyInjection;

public static class WhalePodSharedServiceCollectionExtensions
{
    public static IServiceCollection AddWhalePodSharedServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath,
        bool isDevelopment,
        string connectionString,
        bool includeEmailTransport)
    {
        services.AddWhalePodDbContext(connectionString);
        services.AddWhalePodMediaStorage(configuration, contentRootPath, isDevelopment);
        services.AddWhalePodEmail(configuration, isDevelopment, includeEmailTransport);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<DeviceVerificationFailureTracker>();
        services.AddScoped<DeviceVerificationService>();
        services.AddScoped<PublicContentService>();
        services.AddScoped<PublicSearchService>();
        services.AddScoped<AdminAccessService>();
        services.AddScoped<SessionValidator>();
        services.AddScoped<ServiceAccountAdminService>();
        services.AddScoped<AiSettingsService>();
        services.AddScoped<AiBudgetService>();
        services.AddScoped<SiteModeService>();
        services.AddScoped<ActorGuard>();
        services.AddScoped<PostWriteService>();
        services.AddScoped<CommentWriteService>();
        services.AddScoped<LikeService>();
        services.AddScoped<AcceptanceService>();
        services.AddScoped<ContentModerationService>();
        services.AddScoped<AdminUserService>();
        services.AddScoped<ReportService>();
        services.AddScoped<InviteAdminService>();
        services.AddScoped<CategoryAdminService>();
        services.AddScoped<AuditAdminService>();
        services.AddScoped<ImageUploadService>();
        return services;
    }
}