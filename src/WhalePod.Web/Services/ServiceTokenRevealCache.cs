using Microsoft.Extensions.Caching.Memory;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Web.Services;

public sealed class ServiceTokenRevealCache(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public void Store(Guid adminId, ServiceTokenCreationData token)
    {
        cache.Set(Key(adminId), token, Lifetime);
    }

    public ServiceTokenCreationData? Take(Guid adminId)
    {
        if (!cache.TryGetValue(Key(adminId), out ServiceTokenCreationData? token))
        {
            return null;
        }

        cache.Remove(Key(adminId));
        return token;
    }

    private static string Key(Guid adminId) => $"service-token-reveal:{adminId:N}";
}