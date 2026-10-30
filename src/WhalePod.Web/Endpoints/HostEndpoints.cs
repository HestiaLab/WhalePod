using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;
using WhalePod.Web.Security;

namespace WhalePod.Web.Endpoints;

internal sealed record CopyCounterRequest(Guid ArtifactId);

internal static class HostEndpoints
{
    public static void MapHostEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            service = "whalepod-web"
        }));

        app.MapGet("/healthz/ready", async (
            WhalePodDbContext db,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var readiness = await DatabaseReadiness.CheckAsync(db, cancellationToken);
            if (readiness.Ready)
            {
                return Results.Ok(new { status = "ready", database = "ok" });
            }

            logger.LogWarning("Database readiness check failed: {Reason}.", readiness.Reason);
            return Results.Json(
                new { status = "not_ready", database = "unavailable", reason = readiness.Reason },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/dev/test-login", async (
            HttpContext http,
            IWebHostEnvironment environment,
            WhalePodDbContext db,
            IConfiguration configuration,
            AdminBootstrapService adminBootstrap,
            SiteModeService siteMode,
            AuditRequestContextFactory auditContextFactory) =>
        {
            if (!environment.IsDevelopment() || !configuration.GetValue<bool>("Auth:DevLoginEnabled"))
            {
                return Results.NotFound();
            }

            if (await siteMode.IsLoginDisabledAsync(http.RequestAborted))
            {
                return Results.Redirect("/activate?error=login_disabled");
            }

            var handle = http.Request.Query["handle"].ToString();
            if (!Regex.IsMatch(handle, "^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$"))
            {
                handle = "dev-user";
            }

            var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Handle == handle, http.RequestAborted);
            if (user is null)
            {
                user = new User
                {
                    Handle = handle,
                    DisplayName = "开发测试用户"
                };
                db.Users.Add(user);
                await db.SaveChangesAsync(http.RequestAborted);
            }

            if (user.Status is not (UserStatuses.Active or UserStatuses.Muted))
            {
                return Results.Forbid();
            }

            await adminBootstrap.EnsureAdminAsync(user, auditContextFactory.Create(user.Id), http.RequestAborted);

            var issuedAt = DateTimeOffset.UtcNow;
            var absoluteExpiry = issuedAt.AddHours(configuration.GetValue("Auth:AbsoluteSessionHours", 24));
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Handle),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(SessionClaims.Version, user.SessionVersion.ToString(CultureInfo.InvariantCulture)),
            new Claim(SessionClaims.AbsoluteExpiry, absoluteExpiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new Claim("auth_time", issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            ], CookieAuthenticationDefaults.AuthenticationScheme);

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = absoluteExpiry
                });
            return Results.Redirect(user.ActivatedAt is null ? "/activate" : "/");
        });

        app.MapPost("/activate/redeem", async (
            HttpContext http,
            IAntiforgery antiforgery,
            InviteRedemptionService redemptionService) =>
        {
            if (http.User.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }

            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var outcome = await redemptionService.RedeemAsync(userId, form["code"].ToString(), http.RequestAborted);

            return outcome == InviteRedemptionOutcome.Success
                ? Results.Redirect("/")
                : Results.Redirect("/activate?error=invalid");
        }).RequireAuthorization().RequireRateLimiting("invite-redeem");

        app.MapPost("/logout", async (
            HttpContext http,
            IAntiforgery antiforgery,
            WhalePodDbContext db) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            if (Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                var now = DateTimeOffset.UtcNow;
                await db.Users
                    .Where(user => user.Id == userId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(user => user.SessionVersion, user => user.SessionVersion + 1)
                            .SetProperty(user => user.UpdatedAt, now),
                        http.RequestAborted);
            }

            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        }).RequireAuthorization();


        app.MapPost("/internal/counters/copy", async (
            HttpContext http,
            CopyCounterService counterService,
            ILogger<Program> logger) =>
        {
            if (!WebFormGuard.IsSameOrigin(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            CopyCounterRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<CopyCounterRequest>(http.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            if (request is null || request.ArtifactId == Guid.Empty)
            {
                return Results.BadRequest();
            }

            try
            {
                await counterService.RecordCopyAsync(request.ArtifactId, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Copy counter failed.");
            }

            return Results.NoContent();
        }).DisableAntiforgery().RequireRateLimiting("copy-counter");
    }
}