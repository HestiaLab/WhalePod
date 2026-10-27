using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace WhalePod.Infrastructure.Services;

public sealed class DeviceVerificationFailureTracker(
    IMemoryCache cache,
    TimeProvider timeProvider,
    IOptions<DeviceVerificationOptions> options)
{
    private readonly object _gate = new();

    public int GetLockRemainingSeconds(Guid userId, byte[]? ipHash)
    {
        var key = LockKey(userId, ipHash);
        if (!cache.TryGetValue(key, out LockState? state) || state is null)
        {
            return 0;
        }

        var remaining = state.LockedUntil - timeProvider.GetUtcNow();
        return remaining <= TimeSpan.Zero
            ? 0
            : (int)Math.Ceiling(remaining.TotalSeconds);
    }

    public void RecordFailure(Guid userId, byte[]? ipHash)
    {
        var settings = options.Value;
        var maxAttempts = Math.Max(1, settings.MaxFailedAttempts);
        var window = TimeSpan.FromSeconds(Math.Max(60, settings.FailureWindowSeconds));
        var lockout = TimeSpan.FromSeconds(Math.Max(60, settings.LockoutSeconds));
        var now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            var failureKey = FailureKey(userId, ipHash);
            var failure = cache.TryGetValue(failureKey, out FailureState? state) && state is not null
                ? state
                : null;
            var count = failure is not null && now - failure.WindowStart <= window
                ? failure.Count + 1
                : 1;
            if (count >= maxAttempts)
            {
                cache.Set(LockKey(userId, ipHash), new LockState(now + lockout), lockout);
                cache.Remove(failureKey);
                return;
            }

            cache.Set(failureKey, new FailureState(count, failure?.WindowStart ?? now), window);
        }
    }

    public void Reset(Guid userId, byte[]? ipHash)
    {
        lock (_gate)
        {
            cache.Remove(FailureKey(userId, ipHash));
            cache.Remove(LockKey(userId, ipHash));
        }
    }

    private static string FailureKey(Guid userId, byte[]? ipHash)
    {
        return $"device-fail:{BuildKey(userId, ipHash)}";
    }

    private static string LockKey(Guid userId, byte[]? ipHash)
    {
        return $"device-lock:{BuildKey(userId, ipHash)}";
    }

    private static string BuildKey(Guid userId, byte[]? ipHash)
    {
        return ipHash is null or { Length: 0 }
            ? $"{userId:N}:noip"
            : $"{userId:N}:{Convert.ToHexString(ipHash)}";
    }

    private sealed record FailureState(int Count, DateTimeOffset WindowStart);

    private sealed record LockState(DateTimeOffset LockedUntil);
}