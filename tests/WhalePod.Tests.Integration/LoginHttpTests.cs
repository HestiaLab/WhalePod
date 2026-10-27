using System.Net;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class LoginHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Login_page_and_github_start_honor_configuration_and_login_disabled()
    {
        using var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        using (var login = await client.GetAsync("/login"))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var html = WebUtility.HtmlDecode(await login.Content.ReadAsStringAsync());
            Assert.Contains("登录 WhalePod", html, StringComparison.Ordinal);
            Assert.Contains("GitHub OAuth 尚未配置", html, StringComparison.Ordinal);
        }

        using (var start = await client.GetAsync("/auth/github/start"))
        {
            Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
            Assert.Equal(
                "/login?error=oauth_unconfigured",
                start.Headers.Location?.OriginalString);
        }

        await using var db = ServiceTestSupport.CreateContext();
        var admin = ServiceTestSupport.NewUser("login-disabled-admin", UserRoles.Admin);
        admin.Handle = $"login-disabled-admin-{ServiceTestSupport.NewSuffix()}";
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var siteMode = new SiteModeService(db, new AdminAccessService(db), TimeProvider.System);
        try
        {
            await siteMode.UpdateAsync(
                admin.Id,
                readOnly: false,
                loginDisabled: true,
                ServiceTestSupport.NewAudit(admin.Id));
            using (var blocked = await client.GetAsync("/auth/github/start"))
            {
                Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
                Assert.Equal(
                    "/login?error=login_disabled",
                    blocked.Headers.Location?.OriginalString);
            }
        }
        finally
        {
            await siteMode.UpdateAsync(
                admin.Id,
                readOnly: false,
                loginDisabled: false,
                ServiceTestSupport.NewAudit(admin.Id));
        }
    }
}