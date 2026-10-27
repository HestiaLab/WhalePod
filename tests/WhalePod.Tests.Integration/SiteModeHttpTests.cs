using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class SiteModeHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Read_only_blocks_writes_but_not_browsing_or_admin_mode_switch()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("site-mode-admin", UserRoles.Admin);
        admin.Handle = $"site-mode-admin-{suffix}";
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = new SiteModeService(db, new AdminAccessService(db), TimeProvider.System);
        try
        {
            await service.UpdateAsync(
                admin.Id,
                readOnly: true,
                loginDisabled: false,
                ServiceTestSupport.NewAudit(admin.Id));

            using var webClient = WebFormTestSupport.CreateClient(environment.WebFactory);
            var adminSession = await environment.LoginAsync(admin.Handle);

            using (var home = await SendAsync(webClient, HttpMethod.Get, "/", adminSession))
            {
                Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            }

            using (var blockedPost = await SendAsync(
                       webClient,
                       HttpMethod.Post,
                       "/editor/create",
                       adminSession,
                       new FormUrlEncodedContent(new Dictionary<string, string>
                       {
                           ["title"] = "只读模式不应创建帖子",
                           ["bodyMd"] = "只读模式写入必须被拒绝。"
                       })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, blockedPost.StatusCode);
                Assert.Contains("只读", await blockedPost.Content.ReadAsStringAsync());
            }

            db.ChangeTracker.Clear();
            Assert.Equal(
                0,
                await db.Posts.CountAsync(post => post.Title == "只读模式不应创建帖子"));

            using var apiClient = environment.ApiFactory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false,
                    HandleCookies = false
                });
            using (var blockedApi = await SendAsync(
                       apiClient,
                       HttpMethod.Post,
                       "/api/v1/posts",
                       adminSession,
                       new StringContent("{}", System.Text.Encoding.UTF8, "application/json")))
            {
                Assert.Equal(HttpStatusCode.Forbidden, blockedApi.StatusCode);
                var body = await blockedApi.Content.ReadAsStringAsync();
                Assert.Contains("read_only", body);
            }

            var modeForm = await WebFormTestSupport.LoadFormAsync(
                webClient,
                "/admin/mode",
                adminSession);
            using (var disable = await WebFormTestSupport.PostFormAsync(
                       webClient,
                       "/admin/mode/update",
                       new Dictionary<string, string>
                       {
                           ["__RequestVerificationToken"] = modeForm.Token,
                           ["readOnly"] = "false",
                           ["loginDisabled"] = "false",
                           ["confirmMode"] = "true"
                       },
                       adminSession,
                       modeForm.CsrfCookie))
            {
                Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);
            }

            db.ChangeTracker.Clear();
            Assert.False((await service.GetAsync()).ReadOnly);
        }
        finally
        {
            await service.UpdateAsync(
                admin.Id,
                readOnly: false,
                loginDisabled: false,
                ServiceTestSupport.NewAudit(admin.Id));
        }
    }

    [DatabaseFact]
    public async Task Login_disabled_blocks_new_dev_login_but_keeps_existing_session()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("site-login-admin", UserRoles.Admin);
        admin.Handle = $"site-login-admin-{suffix}";
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = new SiteModeService(db, new AdminAccessService(db), TimeProvider.System);
        try
        {
            var adminSession = await environment.LoginAsync(admin.Handle);
            await service.UpdateAsync(
                admin.Id,
                readOnly: false,
                loginDisabled: true,
                ServiceTestSupport.NewAudit(admin.Id));

            using var anonymous = WebFormTestSupport.CreateClient(environment.WebFactory);
            using (var login = await anonymous.GetAsync(
                       $"/dev/test-login?handle={Uri.EscapeDataString(admin.Handle)}"))
            {
                Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
                Assert.Equal(
                    "/activate?error=login_disabled",
                    login.Headers.Location?.OriginalString);
            }

            using (var existing = await SendAsync(anonymous, HttpMethod.Get, "/admin/mode", adminSession))
            {
                Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
            }
        }
        finally
        {
            await service.UpdateAsync(
                admin.Id,
                readOnly: false,
                loginDisabled: false,
                ServiceTestSupport.NewAudit(admin.Id));
        }
    }

    [DatabaseFact]
    public async Task Read_only_can_preserve_authenticated_member_writes_while_blocking_anonymous()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("site-member-admin", UserRoles.Admin);
        var member = ServiceTestSupport.NewUser("site-member-writer");
        admin.Handle = $"site-member-admin-{suffix}";
        member.Handle = $"site-member-writer-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(admin, member);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var service = new SiteModeService(db, new AdminAccessService(db), TimeProvider.System);
        try
        {
            await service.UpdateAsync(
                admin.Id,
                readOnly: true,
                loginDisabled: false,
                readOnlyAllowMemberWrite: true,
                ServiceTestSupport.NewAudit(admin.Id));

            using var webClient = WebFormTestSupport.CreateClient(environment.WebFactory);
            var memberSession = await environment.LoginAsync(member.Handle);
            var editorForm = await WebFormTestSupport.LoadFormAsync(
                webClient,
                "/editor",
                memberSession);
            var title = $"只读成员写入测试 {suffix}";
            using (var created = await WebFormTestSupport.PostFormAsync(
                       webClient,
                       "/editor/create",
                       new Dictionary<string, string>
                       {
                           ["__RequestVerificationToken"] = editorForm.Token,
                           ["title"] = title,
                           ["categoryId"] = category.Id.ToString(),
                           ["bodyMd"] = "只读模式保留成员写权限时，通过 CSRF 与账号门禁后仍可发帖。"
                       },
                       memberSession,
                       editorForm.CsrfCookie))
            {
                Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            }

            db.ChangeTracker.Clear();
            Assert.True(await db.Posts.AnyAsync(post => post.Title == title));

            using (var anonymous = await SendAsync(
                       webClient,
                       HttpMethod.Post,
                       "/editor/create",
                       null,
                       new FormUrlEncodedContent(new Dictionary<string, string>
                       {
                           ["title"] = "匿名只读写入",
                           ["bodyMd"] = "匿名请求必须被拒绝。"
                       })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
            }

            using var apiClient = environment.ApiFactory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false,
                    HandleCookies = false
                });
            var (csrfToken, csrfCookie) = await environment.ApiFactory.GetCsrfAsync(memberSession);
            var apiTitle = $"只读成员 API 写入测试 {suffix}";
            using (var apiCreated = await SendApiPostAsync(
                       apiClient,
                       "/api/v1/posts",
                       new
                       {
                           title = apiTitle,
                           categoryId = category.Id,
                           bodyMd = "只读模式保留成员写权限时，API 在认证与 CSRF 后仍可创建待审帖子。",
                           tags = Array.Empty<string>()
                       },
                       memberSession,
                       csrfToken,
                       csrfCookie))
            {
                Assert.Equal(HttpStatusCode.Created, apiCreated.StatusCode);
            }

            db.ChangeTracker.Clear();
            Assert.True(await db.Posts.AnyAsync(post => post.Title == apiTitle));
        }
        finally
        {
            await service.UpdateAsync(
                admin.Id,
                readOnly: false,
                loginDisabled: false,
                readOnlyAllowMemberWrite: false,
                ServiceTestSupport.NewAudit(admin.Id));
        }
    }

    private static async Task<HttpResponseMessage> SendApiPostAsync(
        HttpClient client,
        string path,
        object body,
        string sessionCookie,
        string csrfToken,
        string csrfCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        request.Headers.Add("X-CSRF-Token", csrfToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? sessionCookie,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(sessionCookie))
        {
            request.Headers.Add("Cookie", sessionCookie);
        }

        return await client.SendAsync(request);
    }
}