using Microsoft.Extensions.Options;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Services;

public sealed class DeviceChallengeCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DeviceVerificationOptions> options,
    ILogger<DeviceChallengeCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(
            Math.Max(1, options.Value.ChallengeCleanupIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<DeviceVerificationService>();
                    var deleted = await service.CleanupExpiredChallengesAsync(stoppingToken);
                    if (deleted > 0)
                    {
                        logger.LogInformation(
                            "Cleaned {Count} expired device challenges.",
                            deleted);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Device challenge cleanup failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}