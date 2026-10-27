using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;

namespace WhalePod.Tests.Integration;

public sealed class WhalePodApiFactory(
    string keysDirectory,
    IReadOnlyDictionary<string, string?>? overrides = null,
    Action<IWebHostBuilder>? configure = null)
    : WebApplicationFactory<WhalePod.Api.Endpoints.ApiApplicationMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:WhalePod"] = ServiceTestSupport.ConnectionString,
                ["DataProtection:KeysDirectory"] = keysDirectory,
                ["Audit:IpHashSecret"] = "test-only-audit-ip-hash-secret-0123456789",
                ["Email:PollIntervalSeconds"] = "3600"
            };
            if (overrides is not null)
            {
                foreach (var pair in overrides)
                {
                    settings[pair.Key] = pair.Value;
                }
            }

            configuration.AddInMemoryCollection(settings);
        });
        configure?.Invoke(builder);
    }

    public async Task MigrateAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WhalePod.Infrastructure.Persistence.WhalePodDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task<(string Token, string Cookie)> GetCsrfAsync(string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf");
        request.Headers.Add("Cookie", sessionCookie);
        using var response = await CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        }).SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : [];
        var antiforgery = setCookies
            .Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith("whalepod_csrf=", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("data").GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return (token!, antiforgery);
    }
}

public sealed class WhalePodWebFactory(
    string keysDirectory,
    IReadOnlyDictionary<string, string?>? overrides = null)
    : WebApplicationFactory<WhalePod.Web.Security.AdminRequirement>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:WhalePod"] = ServiceTestSupport.ConnectionString,
                ["DataProtection:KeysDirectory"] = keysDirectory,
                ["Audit:IpHashSecret"] = "test-only-audit-ip-hash-secret-0123456789",
                ["Auth:DevLoginEnabled"] = "true"
            };
            if (overrides is not null)
            {
                foreach (var pair in overrides)
                {
                    settings[pair.Key] = pair.Value;
                }
            }

            configuration.AddInMemoryCollection(settings);
        });
    }

    public async Task<string> LoginAsync(string handle)
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        using var response = await client.GetAsync($"/dev/test-login?handle={Uri.EscapeDataString(handle)}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : [];
        return cookies
            .Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith("whalepod_session=", StringComparison.Ordinal));
    }
}

public sealed class ApiHttpEnvironment : IAsyncLifetime
{
    private readonly string _keysDirectory = Path.Combine(
        Path.GetTempPath(),
        $"whalepod-dpkeys-{Guid.NewGuid():N}");
    private readonly string _mediaRoot = Path.Combine(
        Path.GetTempPath(),
        $"whalepod-http-media-{Guid.NewGuid():N}");

    public WhalePodApiFactory ApiFactory { get; }

    public WhalePodWebFactory WebFactory { get; }

    public string MediaRootPath => _mediaRoot;

    public string KeysDirectory => _keysDirectory;

    public ApiHttpEnvironment()
    {
        ApiFactory = new WhalePodApiFactory(_keysDirectory);
        WebFactory = new WhalePodWebFactory(_keysDirectory);
    }

    private readonly Dictionary<string, string?> _previousEnvironment = new();

    public Task InitializeAsync()
    {
        SetEnvironment("ConnectionStrings__WhalePod", ServiceTestSupport.ConnectionString);
        SetEnvironment("DataProtection__KeysDirectory", _keysDirectory);
        SetEnvironment("Audit__IpHashSecret", "test-only-audit-ip-hash-secret-0123456789");
        SetEnvironment("Auth__DevLoginEnabled", "true");
        SetEnvironment("MediaStorage__Provider", "Local");
        SetEnvironment("MediaStorage__LocalRootPath", _mediaRoot);
        SetEnvironment("MediaStorage__PublicBaseUrl", "/media");
        return ApiFactory.MigrateAsync();
    }

    public Task<string> LoginAsync(string handle)
    {
        return WebFactory.LoginAsync(handle);
    }

    public async Task DisposeAsync()
    {
        await WebFactory.DisposeAsync();
        await ApiFactory.DisposeAsync();
        foreach (var pair in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        foreach (var directory in new[] { _keysDirectory, _mediaRoot })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void SetEnvironment(string name, string value)
    {
        if (!_previousEnvironment.ContainsKey(name))
        {
            _previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
    }
}

[CollectionDefinition("api-http", DisableParallelization = true)]
public sealed class ApiHttpCollection : ICollectionFixture<ApiHttpEnvironment>;

[Collection("api-http")]
public sealed class ApiHttpTests(ApiHttpEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [DatabaseFact]
    public async Task Write_requires_authentication_and_csrf_and_accepts_shared_session_cookie()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("http-member");
        member.Handle = $"http-member-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        db.Users.Add(member);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var title = $"HTTP 写入测试帖子 {suffix}";
        var body = new
        {
            title,
            categoryId = category.Id,
            bodyMd = "这是通过 HTTP 端点提交的测试正文，长度足够。",
            tags = Array.Empty<string>()
        };

        using (var anonymous = await PostJsonAsync(client, "/api/v1/posts", body, null, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("AUTH_REQUIRED", await ReadCodeAsync(anonymous));
        }

        if (await db.Posts.AnyAsync(post => post.Title == title))
        {
            Assert.Fail("Unauthenticated request created a post.");
        }

        var sessionCookie = await environment.LoginAsync(member.Handle);
        var (csrfToken, antiforgeryCookie) = await environment.ApiFactory.GetCsrfAsync(sessionCookie);

        using (var missingCsrf = await PostJsonAsync(
                   client,
                   "/api/v1/posts",
                   body,
                   sessionCookie,
                   null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(missingCsrf));
        }

        using (var wrongCsrf = await PostJsonAsync(
                   client,
                   "/api/v1/posts",
                   body,
                   sessionCookie,
                   "not-the-right-token"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongCsrf.StatusCode);
        }

        Assert.False(await db.Posts.AnyAsync(post => post.Title == title));

        Guid postId;
        using (var created = await PostJsonAsync(
                   client,
                   "/api/v1/posts",
                   body,
                   sessionCookie,
                   csrfToken,
                   antiforgeryCookie))
        {
            var createdBody = await created.Content.ReadAsStringAsync();
            Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
            using var document = JsonDocument.Parse(createdBody);
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(ModerationStatuses.Pending, data.GetProperty("moderationStatus").GetString());
            postId = data.GetProperty("id").GetGuid();
            Assert.Equal("\"v1\"", created.Headers.ETag?.ToString());
        }

        using (var anonymousDetail = await GetAsync(client, $"/api/v1/posts/{postId}", null, null))
        {
            Assert.Equal(HttpStatusCode.NotFound, anonymousDetail.StatusCode);
        }

        using (var authorDetail = await GetAsync(
                   client,
                   $"/api/v1/posts/{postId}",
                   sessionCookie,
                   null))
        {
            Assert.Equal(HttpStatusCode.OK, authorDetail.StatusCode);
            using var document = JsonDocument.Parse(await authorDetail.Content.ReadAsStringAsync());
            Assert.Equal(
                ModerationStatuses.Pending,
                document.RootElement.GetProperty("data").GetProperty("moderationStatus").GetString());
        }
    }

    [DatabaseFact]
    public async Task Admin_endpoints_return_json_403_for_non_admin_and_work_for_admin()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("http-admin", UserRoles.Admin);
        admin.Handle = $"http-admin-{suffix}";
        var member = ServiceTestSupport.NewUser("http-admin-member");
        member.Handle = $"http-admin-member-{suffix}";
        var target = ServiceTestSupport.NewUser("http-admin-target");
        var pending = ServiceTestSupport.NewUser("http-admin-pending", activated: false);
        pending.Handle = $"http-admin-pending-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(admin, member, target, pending);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var memberSession = await environment.LoginAsync(member.Handle);
        var (memberCsrf, memberAntiforgery) = await environment.ApiFactory.GetCsrfAsync(memberSession);
        using (var forbidden = await PostJsonAsync(
                   client,
                   $"/api/v1/admin/users/{target.Id}/status",
                   new { status = "muted", reason = "普通成员不能操作", expiresAt = DateTimeOffset.UtcNow.AddDays(1) },
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(forbidden));
        }

        using (var listForbidden = await GetAsync(client, "/api/v1/admin/users", memberSession, null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, listForbidden.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(listForbidden));
        }

        using (var queueForbidden = await GetAsync(
                   client,
                   "/api/v1/admin/moderation/queue",
                   memberSession,
                   null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, queueForbidden.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(queueForbidden));
        }

        var pendingSession = await environment.LoginAsync(pending.Handle);
        var (pendingCsrf, pendingAntiforgery) = await environment.ApiFactory.GetCsrfAsync(pendingSession);
        using (var pendingWrite = await PostJsonAsync(
                   client,
                   "/api/v1/posts",
                   new
                   {
                       title = "待激活用户 HTTP 发帖",
                       categoryId = category.Id,
                       bodyMd = "待激活用户不能通过 HTTP 写入内容。"
                   },
                   pendingSession,
                   pendingCsrf,
                   pendingAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, pendingWrite.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(pendingWrite));
            using var document = JsonDocument.Parse(await pendingWrite.Content.ReadAsStringAsync());
            Assert.Equal(
                "activation_required",
                document.RootElement.GetProperty("details").GetProperty("reason").GetString());
        }

        var adminSession = await environment.LoginAsync(admin.Handle);
        var (adminCsrf, adminAntiforgery) = await environment.ApiFactory.GetCsrfAsync(adminSession);
        using (var muted = await PostJsonAsync(
                   client,
                   $"/api/v1/admin/users/{target.Id}/status",
                   new
                   {
                       status = "muted",
                       reason = "HTTP 管理操作",
                       expiresAt = DateTimeOffset.UtcNow.AddDays(3)
                   },
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            var mutedBody = await muted.Content.ReadAsStringAsync();
            Assert.True(muted.StatusCode == HttpStatusCode.OK, mutedBody);
            using var document = JsonDocument.Parse(mutedBody);
            Assert.Equal(
                UserStatuses.Muted,
                document.RootElement.GetProperty("data").GetProperty("status").GetString());
        }

        using var detail = await GetAsync(client, $"/api/v1/admin/users/{target.Id}", adminSession, null);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.DoesNotContain("@example.com", await detail.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        object body,
        string? sessionCookie,
        string? csrfToken,
        string? antiforgeryCookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        if (sessionCookie is not null)
        {
            var cookie = antiforgeryCookie is null
                ? sessionCookie
                : $"{sessionCookie}; {antiforgeryCookie}";
            request.Headers.Add("Cookie", cookie);
        }

        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrfToken);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string? sessionCookie,
        string? antiforgeryCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (sessionCookie is not null)
        {
            var cookie = antiforgeryCookie is null
                ? sessionCookie
                : $"{sessionCookie}; {antiforgeryCookie}";
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return document.RootElement.GetProperty("code").GetString();
    }
}