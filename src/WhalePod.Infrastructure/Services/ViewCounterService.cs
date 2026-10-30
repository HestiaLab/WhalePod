using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class ViewCounterService(
    WhalePodDbContext db,
    IOptions<ViewCounterOptions> options,
    TimeProvider timeProvider,
    ILogger<ViewCounterService> logger)
{
    private static readonly ConcurrentDictionary<string, long> Dedupe = new(StringComparer.Ordinal);
    private static int _missingSecretLogged;
    private static long _nextPruneUnix;

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
            var expiresAtUnix = now.ToUnixTimeSeconds() + (windowSeconds * 2);
            PruneDedupe(now.ToUnixTimeSeconds());
            if (!Dedupe.TryAdd(cacheKey, expiresAtUnix))
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
                Dedupe.TryRemove(cacheKey, out _);
                return false;
            }

            await db.Posts
                .Where(post => post.Id == postId)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(post => post.ViewCount, post => post.ViewCount + 1),
                    cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to record a view for post {PostId}.", postId);
            return false;
        }
    }

    private static void PruneDedupe(long nowUnix)
    {
        var nextPrune = Interlocked.Read(ref _nextPruneUnix);
        if (nowUnix < nextPrune
            || Interlocked.CompareExchange(ref _nextPruneUnix, nowUnix + 60, nextPrune) != nextPrune)
        {
            return;
        }

        foreach (var pair in Dedupe)
        {
            if (pair.Value <= nowUnix)
            {
                Dedupe.TryRemove(pair.Key, out _);
            }
        }
    }
}