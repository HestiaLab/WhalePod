using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class ViewCounterService(
    WhalePodDbContext db,
    IMemoryCache cache,
    IOptions<ViewCounterOptions> options,
    TimeProvider timeProvider,
    ILogger<ViewCounterService> logger)
{
    private static int _missingSecretLogged;

    public async Task<bool> RecordViewAsync(
        Guid postId,
        string? clientIp,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = options.Value;
            if (string.IsNullOrWhiteSpace(settings.DedupeSecret))
            {
                if (Interlocked.Exchange(ref _missingSecretLogged, 1) == 0)
                {
                    logger.LogWarning("ViewCounter:DedupeSecret is not configured; view counting is disabled.");
                }

                return false;
            }

            var windowSeconds = Math.Max(60, settings.DedupeWindowSeconds);
            var now = timeProvider.GetUtcNow();
            var bucket = now.ToUnixTimeSeconds() / windowSeconds;
            var material = Encoding.UTF8.GetBytes($"view|{clientIp ?? "unknown"}|{postId:N}|{bucket}");
            var keyHash = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.DedupeSecret), material));
            var cacheKey = $"view:{keyHash}";
            if (cache.TryGetValue(cacheKey, out _))
            {
                return false;
            }

            var isPublic = await db.Posts
                .AsNoTracking()
                .AnyAsync(post => post.Id == postId
                    && post.ModerationStatus == ModerationStatuses.Approved
                    && post.DeletedAt == null, cancellationToken);
            if (!isPublic)
            {
                return false;
            }

            await db.Posts
                .Where(post => post.Id == postId)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(post => post.ViewCount, post => post.ViewCount + 1),
                    cancellationToken);
            cache.Set(cacheKey, true, TimeSpan.FromSeconds(windowSeconds * 2));
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to record a view for post {PostId}.", postId);
            return false;
        }
    }
}