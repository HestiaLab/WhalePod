using Microsoft.Extensions.Caching.Memory;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Web.Services;

public sealed class InviteRevealCache(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public void Store(Guid adminId, InviteCreationData invite)
    {
        cache.Set(Key(adminId), invite, Lifetime);
    }

    public InviteCreationData? Take(Guid adminId)
    {
        if (!cache.TryGetValue(Key(adminId), out InviteCreationData? invite))
        {
            return null;
        }

        cache.Remove(Key(adminId));
        return invite;
    }

    private static string Key(Guid adminId) => $"invite-reveal:{adminId:N}";
}