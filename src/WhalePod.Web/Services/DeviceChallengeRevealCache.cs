using Microsoft.Extensions.Caching.Memory;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Web.Services;

public sealed class DeviceChallengeRevealCache(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(6);

    public void Store(Guid userId, DeviceChallengeView challenge)
    {
        cache.Set(Key(userId), challenge, Lifetime);
    }

    public DeviceChallengeView? Take(Guid userId)
    {
        if (!cache.TryGetValue(Key(userId), out DeviceChallengeView? challenge))
        {
            return null;
        }

        cache.Remove(Key(userId));
        return challenge;
    }

    private static string Key(Guid userId) => $"device-challenge-reveal:{userId:N}";
}