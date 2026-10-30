using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class AdminApiHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Admin_invite_and_category_api_enforces_auth_csrf_and_validation()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("api-admin", UserRoles.Admin);
        admin.Handle = $"api-admin-{suffix}";
        var member = ServiceTestSupport.NewUser("api-member");
        member.Handle = $"api-member-{suffix}";
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.AddRange(admin, member);
            await db.SaveChangesAsync();
        }

        using var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using (var anonymous = await client.GetAsync("/api/v1/admin/invites"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var memberSession = await environment.LoginAsync(member.Handle);
        var (memberCsrf, memberAntiforgery) = await environment.ApiFactory.GetCsrfAsync(memberSession);
        using (var forbiddenList = await GetAsync(client, "/api/v1/admin/invites", memberSession))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenList.StatusCode);
        }

        using (var forbiddenCreate = await SendJsonAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/admin/invites",
                   new { note = "成员不能创建", maxUses = 1 },
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenCreate.StatusCode);
        }

        var adminSession = await environment.LoginAsync(admin.Handle);
        var (adminCsrf, adminAntiforgery) = await environment.ApiFactory.GetCsrfAsync(adminSession);
        using (var missingCsrf = await SendJsonAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/admin/invites",
                   new { note = "缺少 CSRF", maxUses = 1 },
                   adminSession,
                   null,
                   null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        }

        var inviteIds = new List<Guid>();
        var inviteHints = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            using var created = await SendJsonAsync(
                client,
                HttpMethod.Post,
                "/api/v1/admin/invites",
                new { note = $"API 邀请 {index}", maxUses = 2 },
                adminSession,
                adminCsrf,
                adminAntiforgery);
            var body = await created.Content.ReadAsStringAsync();
            Assert.True(created.StatusCode == HttpStatusCode.Created, body);
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            inviteIds.Add(data.GetProperty("id").GetGuid());
            inviteHints.Add(data.GetProperty("codeHint").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("plaintextCode").GetString()));
            Assert.Equal("active", data.GetProperty("status").GetString());
        }

        using (var invalidCreate = await SendJsonAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/admin/invites",
                   new { note = "非法次数", maxUses = 0 },
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
        }

        using (var firstPage = await GetAsync(
                   client,
                   "/api/v1/admin/invites?status=active&limit=1",
                   adminSession))
        {
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            using var document = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.Single(data.GetProperty("items").EnumerateArray());
            var nextCursor = data.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(nextCursor));

            using var secondPage = await GetAsync(
                client,
                $"/api/v1/admin/invites?status=active&limit=1&cursor={Uri.EscapeDataString(nextCursor!)}",
                adminSession);
            Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
            using var secondDocument = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
            var secondData = secondDocument.RootElement.GetProperty("data");
            var secondId = secondData.GetProperty("items")[0].GetProperty("id").GetGuid();
            Assert.NotEqual(inviteIds[0], secondId);
        }

        using (var invalidCursor = await GetAsync(
                   client,
                   "/api/v1/admin/invites?cursor=not-a-cursor",
                   adminSession))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        }

        using (var disabled = await SendJsonAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/admin/invites/{inviteIds[0]}/disable",
                   new { },
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            using var document = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());
            Assert.Equal("disabled", document.RootElement.GetProperty("data").GetProperty("status").GetString());
        }

        var slug = $"api-cat-{suffix}";
        using (var categoryCreated = await SendJsonAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/admin/categories",
                   new { slug, name = "API 分类", description = "API 创建", displayOrder = 7 },
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            var body = await categoryCreated.Content.ReadAsStringAsync();
            Assert.True(categoryCreated.StatusCode == HttpStatusCode.Created, body);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(slug, document.RootElement.GetProperty("data").GetProperty("slug").GetString());
        }

        using (var duplicate = await SendJsonAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/admin/categories",
                   new { slug, name = "重复分类" },
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        Guid categoryId;
        using (var categories = await GetAsync(client, "/api/v1/admin/categories", adminSession))
        {
            Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
            using var document = JsonDocument.Parse(await categories.Content.ReadAsStringAsync());
            categoryId = document.RootElement.GetProperty("data").GetProperty("items")
                .EnumerateArray()
                .Single(item => item.GetProperty("slug").GetString() == slug)
                .GetProperty("id")
                .GetGuid();
        }

        using (var patched = await SendJsonAsync(
                   client,
                   HttpMethod.Patch,
                   $"/api/v1/admin/categories/{categoryId}",
                   new { name = "API 分类（已更新）", status = "disabled" },
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            var body = await patched.Content.ReadAsStringAsync();
            Assert.True(patched.StatusCode == HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            Assert.Equal("API 分类（已更新）", data.GetProperty("name").GetString());
            Assert.Equal("disabled", data.GetProperty("status").GetString());
        }

        using (var unknownField = await SendJsonAsync(
                   client,
                   HttpMethod.Patch,
                   $"/api/v1/admin/categories/{categoryId}",
                   new { unknown = "value" },
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        }

        using (var invalidStatus = await SendJsonAsync(
                   client,
                   HttpMethod.Patch,
                   $"/api/v1/admin/categories/{categoryId}",
                   new { status = "hidden" },
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task Admin_audit_api_filters_paginates_and_enforces_admin()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("api-audit-admin", UserRoles.Admin);
        admin.Handle = $"api-audit-admin-{suffix}";
        var member = ServiceTestSupport.NewUser("api-audit-member");
        member.Handle = $"api-audit-member-{suffix}";
        var action = $"api-audit-{suffix}.event";
        var now = DateTimeOffset.UtcNow;
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.AddRange(admin, member);
            for (var index = 0; index < 3; index++)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    ActorType = AuditActorTypes.User,
                    ActorId = admin.Id,
                    Action = action,
                    TargetType = "user",
                    TargetId = admin.Id,
                    RequestId = $"api-audit-{index}",
                    IpHash = new byte[32],
                    MetadataJson = JsonDocument.Parse("{}"),
                    CreatedAt = now.AddMinutes(-index - 1)
                });
            }

            await db.SaveChangesAsync();
        }

        using var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using (var anonymous = await client.GetAsync($"/api/v1/admin/audit?action={action}"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var memberSession = await environment.LoginAsync(member.Handle);
        using (var forbidden = await GetAsync(
                   client,
                   $"/api/v1/admin/audit?action={action}",
                   memberSession))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var adminSession = await environment.LoginAsync(admin.Handle);
        using (var firstPage = await GetAsync(
                   client,
                   $"/api/v1/admin/audit?action={action}&limit=1",
                   adminSession))
        {
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            using var document = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            var items = data.GetProperty("items").EnumerateArray().ToList();
            Assert.Single(items);
            Assert.Equal(action, items[0].GetProperty("action").GetString());
            Assert.True(data.GetProperty("nextCursor").GetString() is not null);

            using var secondPage = await GetAsync(
                client,
                $"/api/v1/admin/audit?action={action}&limit=1&cursor={Uri.EscapeDataString(data.GetProperty("nextCursor").GetString()!)}",
                adminSession);
            Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
            using var secondDocument = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
            var secondItem = secondDocument.RootElement.GetProperty("data").GetProperty("items")[0];
            Assert.NotEqual(
                items[0].GetProperty("id").GetGuid(),
                secondItem.GetProperty("id").GetGuid());
        }

        using (var invalidActorType = await GetAsync(
                   client,
                   "/api/v1/admin/audit?actorType=unknown",
                   adminSession))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidActorType.StatusCode);
        }

        using (var invalidCursor = await GetAsync(
                   client,
                   "/api/v1/admin/audit?cursor=invalid",
                   adminSession))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string? sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (sessionCookie is not null)
        {
            request.Headers.Add("Cookie", sessionCookie);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        string? sessionCookie,
        string? csrfToken,
        string? antiforgeryCookie)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };
        if (sessionCookie is not null)
        {
            request.Headers.Add(
                "Cookie",
                antiforgeryCookie is null
                    ? sessionCookie
                    : $"{sessionCookie}; {antiforgeryCookie}");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrfToken);
        }

        return await client.SendAsync(request);
    }
}