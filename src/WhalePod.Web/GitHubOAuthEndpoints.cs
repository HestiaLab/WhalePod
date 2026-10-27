using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Services;
using WhalePod.Web.Security;
using WhalePod.Web.Services;

namespace WhalePod.Web;

internal static class GitHubOAuthEndpoints
{
    private const string StateCookieName = "whalepod_oauth_state";

    public static void MapGitHubOAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/github/start", async (
            HttpContext http,
            GitHubOAuthService oauth,
            GitHubOAuthStateProtector stateProtector,
            SiteModeService siteMode,
            IOptions<GitHubOAuthOptions> options) =>
        {
            if (await siteMode.IsLoginDisabledAsync(http.RequestAborted))
            {
                return Results.Redirect("/login?error=login_disabled");
            }

            if (!oauth.IsConfigured)
            {
                return Results.Redirect("/login?error=oauth_unconfigured");
            }

            var returnUrl = http.Request.Query["returnUrl"].ToString();
            if (!GitHubOAuthService.IsSafeReturnUrl(returnUrl))
            {
                returnUrl = "/";
            }

            var state = GitHubOAuthService.NewState();
            var now = DateTimeOffset.UtcNow;
            http.Response.Cookies.Append(
                StateCookieName,
                stateProtector.Protect(state, returnUrl, now),
                new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = http.Request.IsHttps,
                    MaxAge = TimeSpan.FromMinutes(10),
                    Path = options.Value.CallbackPath
                });
            var redirectUri = BuildRedirectUri(http, options.Value.CallbackPath);
            return Results.Redirect(oauth.BuildAuthorizeUrl(redirectUri, state));
        });

        app.MapGet("/auth/github/callback", async (
            HttpContext http,
            GitHubOAuthService oauth,
            GitHubOAuthStateProtector stateProtector,
            SiteModeService siteMode,
            AuditRequestContextFactory auditContextFactory,
            IOptions<GitHubOAuthOptions> options,
            ILogger<Program> logger) =>
        {
            var stateCookie = http.Request.Cookies[StateCookieName];
            http.Response.Cookies.Delete(
                StateCookieName,
                new CookieOptions { Path = options.Value.CallbackPath });

            if (await siteMode.IsLoginDisabledAsync(http.RequestAborted))
            {
                return Results.Redirect("/login?error=login_disabled");
            }

            if (!string.IsNullOrWhiteSpace(http.Request.Query["error"]))
            {
                return Results.Redirect("/login?error=oauth_denied");
            }

            var code = http.Request.Query["code"].ToString();
            var state = http.Request.Query["state"].ToString();
            var payload = stateProtector.Unprotect(stateCookie);
            if (string.IsNullOrWhiteSpace(code)
                || payload is null
                || !string.Equals(payload.State, state, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "GitHub OAuth callback rejected for invalid state or missing code requestId={RequestId}.",
                    http.TraceIdentifier);
                return Results.Redirect("/login?error=state");
            }

            try
            {
                var accessToken = await oauth.ExchangeCodeAsync(code, http.RequestAborted);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    return Results.Redirect("/login?error=oauth_exchange");
                }

                var profile = await oauth.GetProfileAsync(accessToken, http.RequestAborted);
                if (profile is null)
                {
                    return Results.Redirect("/login?error=oauth_profile");
                }

                var result = await oauth.SignInAsync(
                    profile,
                    userId => auditContextFactory.Create(userId),
                    http.RequestAborted);
                if (result.Outcome != GitHubSignInOutcome.Success || result.User is null)
                {
                    return Results.Redirect($"/login?error={MapLoginError(result.Outcome)}");
                }

                var user = result.User;
                var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Handle),
                    new Claim(ClaimTypes.Role, user.Role),
                    new Claim("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
                ], CookieAuthenticationDefaults.AuthenticationScheme);
                await http.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity),
                    new AuthenticationProperties
                    {
                        IsPersistent = false,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                    });

                var returnUrl = GitHubOAuthService.IsSafeReturnUrl(payload.ReturnUrl)
                    ? payload.ReturnUrl
                    : "/";
                return Results.Redirect(user.ActivatedAt is null ? "/activate" : returnUrl);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(
                    exception,
                    "GitHub OAuth callback failed to reach GitHub requestId={RequestId}.",
                    http.TraceIdentifier);
                return Results.Redirect("/login?error=oauth_unavailable");
            }
            catch (JsonException exception)
            {
                logger.LogWarning(
                    exception,
                    "GitHub OAuth callback received invalid JSON requestId={RequestId}.",
                    http.TraceIdentifier);
                return Results.Redirect("/login?error=oauth_profile");
            }
        });
    }

    private static string BuildRedirectUri(HttpContext http, string callbackPath)
    {
        var path = callbackPath.StartsWith('/') ? callbackPath : $"/{callbackPath}";
        return $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{path}";
    }

    private static string MapLoginError(GitHubSignInOutcome outcome)
    {
        return outcome switch
        {
            GitHubSignInOutcome.NotAllowed => "not_allowed",
            GitHubSignInOutcome.AccountBanned => "account_banned",
            GitHubSignInOutcome.AccountDeleted => "account_deleted",
            GitHubSignInOutcome.AccountInactive => "account_inactive",
            _ => "oauth_signin"
        };
    }
}