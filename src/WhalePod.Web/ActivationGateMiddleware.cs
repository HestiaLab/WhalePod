using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Web;

public sealed class ActivationGateMiddleware(RequestDelegate next)
{
    private static readonly PathString[] AllowedPrefixes =
    [
        new("/activate"),
        new("/logout"),
        new("/dev/test-login"),
        new("/healthz"),
        new("/_framework"),
        new("/_blazor"),
        new("/app.css"),
        new("/WhalePod.Web.styles.css"),
        new("/favicon")
    ];

    public async Task InvokeAsync(HttpContext context, WhalePodDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (AllowedPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
        {
            await next(context);
            return;
        }

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/activate");
            return;
        }

        var state = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Status, user.ActivatedAt })
            .SingleOrDefaultAsync(context.RequestAborted);

        if (state is null
            || state.Status is UserStatuses.Banned or UserStatuses.Deleted)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/activate");
            return;
        }

        if (state.ActivatedAt is null)
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.Redirect("/activate");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (state.Status == UserStatuses.Muted && !IsAllowedWhileMuted(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool IsAllowedWhileMuted(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return true;
        }

        return request.Path.StartsWithSegments("/logout")
            || request.Path.StartsWithSegments("/settings")
            || request.Path.StartsWithSegments("/internal/counters/copy");
    }
}