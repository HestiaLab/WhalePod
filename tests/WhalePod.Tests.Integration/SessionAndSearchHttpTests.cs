using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class SessionAndSearchHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Logout_revokes_old_cookie_for_api_and_web()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("session-member");
        member.Handle = $"session-member-{suffix}";
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.Add(member);
            await db.SaveChangesAsync();
        }

        using var webClient = WebFormTestSupport.CreateClient(environment.WebFactory);
        using var apiClient = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var cookie = await environment.LoginAsync(member.Handle);
        using (var before = await GetApiCsrfAsync(apiClient, cookie))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        var form = await WebFormTestSupport.LoadFormAsync(webClient, "/activate", cookie);
        using (var logout = await WebFormTestSupport.PostFormAsync(
                   webClient,
                   "/logout",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = form.Token
                   },
                   cookie,
                   form.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.Equal(
                2,
                await db.Users.AsNoTracking()
                    .Where(user => user.Id == member.Id)
                    .Select(user => user.SessionVersion)
                    .SingleAsync());
        }

        using (var after = await GetApiCsrfAsync(apiClient, cookie))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }

        using (var webAfter = await GetWithCookieAsync(webClient, "/settings/profile", cookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, webAfter.StatusCode);
            Assert.Contains("/login", webAfter.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        var newCookie = await environment.LoginAsync(member.Handle);
        using (var relogin = await GetApiCsrfAsync(apiClient, newCookie))
        {
            Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task Session_validator_rejects_version_mismatch_and_expired_absolute_session()
    {
        var member = ServiceTestSupport.NewUser("session-validator");
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.Add(member);
            await db.SaveChangesAsync();
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var validator = new SessionValidator(db, TimeProvider.System);
            Assert.True(await validator.IsValidAsync(NewPrincipal(member, member.SessionVersion, 60)));
            Assert.False(await validator.IsValidAsync(NewPrincipal(member, member.SessionVersion + 1, 60)));
            Assert.False(await validator.IsValidAsync(NewPrincipal(member, member.SessionVersion, -60)));
            Assert.False(await validator.IsValidAsync(new ClaimsPrincipal(new ClaimsIdentity())));
        }
    }

    [DatabaseFact]
    public async Task Api_search_is_rate_limited_and_invalid_web_cursor_is_not_500()
    {
        using var apiClient = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var negativeTicks = Base64Url(JsonSerializer.Serialize(new
        {
            s = "latest",
            t = -1L,
            i = Guid.NewGuid()
        }));
        using var webClient = WebFormTestSupport.CreateClient(environment.WebFactory);
        using (var feed = await webClient.GetAsync($"/?cursor={Uri.EscapeDataString(negativeTicks)}"))
        {
            Assert.NotEqual(HttpStatusCode.InternalServerError, feed.StatusCode);
        }

        var searchCursor = Base64Url(JsonSerializer.Serialize(new
        {
            s = "posts",
            t = -1L,
            r = 0,
            i = Guid.NewGuid(),
            q = "deadbeefdeadbeef"
        }));
        using (var search = await apiClient.GetAsync(
                   $"/api/v1/search?q=test&type=posts&cursor={Uri.EscapeDataString(searchCursor)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, search.StatusCode);
        }

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? throttled = null;
        for (var index = 0; index < 35; index++)
        {
            var response = await apiClient.GetAsync($"/api/v1/search?q=session-search-{index}&type=posts");
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttled ??= response;
            }
            else
            {
                response.Dispose();
            }
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.NotNull(throttled);
        Assert.True(throttled!.Headers.RetryAfter is not null);
    }

    private static ClaimsPrincipal NewPrincipal(User user, int version, int expiryOffsetSeconds)
    {
        var expiry = DateTimeOffset.UtcNow.AddSeconds(expiryOffsetSeconds).ToUnixTimeSeconds();
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(SessionClaims.Version, version.ToString()),
            new Claim(SessionClaims.AbsoluteExpiry, expiry.ToString())
        ], "Test");
        return new ClaimsPrincipal(identity);
    }

    private static string Base64Url(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static async Task<HttpResponseMessage> GetApiCsrfAsync(
        HttpClient client,
        string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf");
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(
        HttpClient client,
        string path,
        string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }
}