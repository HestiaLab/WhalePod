using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Services;

internal sealed class MediaUploadCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<MediaUploadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("MediaStorage:CleanupIntervalMinutes", 60);
        if (intervalMinutes < 1)
        {
            intervalMinutes = 60;
        }

        var batchSize = configuration.GetValue("MediaStorage:CleanupBatchSize", 100);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
                    await cleanup.CleanupOnceAsync(batchSize, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Scheduled media upload cleanup failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}