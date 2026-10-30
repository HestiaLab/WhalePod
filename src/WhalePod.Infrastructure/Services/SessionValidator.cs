using System.Globalization;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public static class SessionClaims
{
    public const string Version = "session_version";
    public const string AbsoluteExpiry = "session_abs";
}

public sealed class SessionValidator(WhalePodDbContext db, TimeProvider timeProvider)
{
    public async Task<bool> IsValidAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken = default)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!Guid.TryParse(
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                out var userId))
        {
            return false;
        }

        if (!int.TryParse(
                principal.FindFirst(SessionClaims.Version)?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var version)
            || !long.TryParse(
                principal.FindFirst(SessionClaims.AbsoluteExpiry)?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiryUnix))
        {
            return false;
        }

        if (expiryUnix <= timeProvider.GetUtcNow().ToUnixTimeSeconds())
        {
            return false;
        }

        var currentVersion = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => (int?)user.SessionVersion)
            .SingleOrDefaultAsync(cancellationToken);
        return currentVersion == version;
    }
}