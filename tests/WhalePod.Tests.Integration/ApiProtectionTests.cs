using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;

namespace WhalePod.Tests.Integration;

public sealed class ApiProtectionEnvironment : IAsyncLifetime
{
    private readonly string _keysDirectory = Path.Combine(
        Path.GetTempPath(),
        $"whalepod-protection-dpkeys-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _previousEnvironment = new();

    public ApiProtectionEnvironment()
    {
        SetEnvironment("ConnectionStrings__WhalePod", ServiceTestSupport.ConnectionString);
        SetEnvironment("DataProtection__KeysDirectory", _keysDirectory);
        SetEnvironment("Audit__IpHashSecret", "test-only-audit-ip-hash-secret-0123456789");
        SetEnvironment("Auth__DevLoginEnabled", "true");
        SetEnvironment("ApiWriteRateLimit__PermitLimit", "2");
        SetEnvironment("ApiWriteRateLimit__WindowSeconds", "60");
        SetEnvironment("ApiRequestLimits__MaxJsonBodyBytes", "2048");
        ApiFactory = new WhalePodApiFactory(
            _keysDirectory,
            new Dictionary<string, string?>
            {
                ["ApiWriteRateLimit:PermitLimit"] = "2",
                ["ApiWriteRateLimit:WindowSeconds"] = "60",
                ["ApiRequestLimits:MaxJsonBodyBytes"] = "2048"
            });
        WebFactory = new WhalePodWebFactory(_keysDirectory);
    }

    public WhalePodApiFactory ApiFactory { get; }

    public WhalePodWebFactory WebFactory { get; }

    public Task InitializeAsync()
    {
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

        try
        {
            Directory.Delete(_keysDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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

public sealed class ApiProtectionTests(ApiProtectionEnvironment environment) : IClassFixture<ApiProtectionEnvironment>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [DatabaseFact]
    public async Task Write_rate_limit_returns_json_429_with_retry_after_and_no_extra_side_effects()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("limit-member");
        member.Handle = $"limit-member-{suffix}";
        var other = ServiceTestSupport.NewUser("limit-other");
        other.Handle = $"limit-other-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(member, other);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var session = await environment.LoginAsync(member.Handle);
        var (csrfToken, antiforgeryCookie) = await environment.ApiFactory.GetCsrfAsync(session);
        var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var titles = new[] { $"限流帖子一 {suffix}", $"限流帖子二 {suffix}", $"限流帖子三 {suffix}" };
        for (var index = 0; index < 2; index++)
        {
            using var response = await PostPostAsync(
                client,
                new
                {
                    title = titles[index],
                    categoryId = category.Id,
                    bodyMd = "限流测试正文，长度足够且不会写入数据库。"
                },
                session,
                csrfToken,
                antiforgeryCookie);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var limited = await PostPostAsync(
            client,
            new
            {
                title = titles[2],
                categoryId = category.Id,
                bodyMd = "这次请求应被限流拒绝，不能创建帖子。"
            },
            session,
            csrfToken,
            antiforgeryCookie);
        var limitedBody = await limited.Content.ReadAsStringAsync();
        Assert.True(limited.StatusCode == HttpStatusCode.TooManyRequests, limitedBody);
        Assert.Equal("application/json", limited.Content.Headers.ContentType?.MediaType);
        Assert.True(limited.Headers.RetryAfter is not null);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        using (var document = JsonDocument.Parse(limitedBody))
        {
            Assert.Equal("RATE_LIMITED", document.RootElement.GetProperty("code").GetString());
            var retryAfterSeconds = document.RootElement
                .GetProperty("details")
                .GetProperty("retryAfterSeconds")
                .GetInt32();
            Assert.InRange(retryAfterSeconds, 1, 60);
        }

        Assert.Equal(2, await db.Posts.CountAsync(post => titles.Contains(post.Title)));
        Assert.Equal(
            2,
            await db.AuditLogs.CountAsync(log =>
                log.ActorId == member.Id && log.Action == "post.created"));

        using (var feed = await client.GetAsync("/api/v1/feed"))
        {
            Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
            var feedJson = await feed.Content.ReadAsStringAsync();
            Assert.DoesNotContain("firstArtifactId", feedJson, StringComparison.Ordinal);
        }

        using (var myPosts = await GetAsync(client, "/api/v1/me/posts", session))
        {
            Assert.Equal(HttpStatusCode.OK, myPosts.StatusCode);
        }

        var otherSession = await environment.LoginAsync(other.Handle);
        var (otherCsrf, otherAntiforgery) = await environment.ApiFactory.GetCsrfAsync(otherSession);
        using (var otherPost = await PostPostAsync(
                   client,
                   new
                   {
                       title = $"限流其他用户 {suffix}",
                       categoryId = category.Id,
                       bodyMd = "按用户分区后，其他用户不应被前一用户的限流影响。"
                   },
                   otherSession,
                   otherCsrf,
                   otherAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Created, otherPost.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task Oversized_json_body_returns_validation_envelope_without_creating_content()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("body-limit-member");
        member.Handle = $"body-limit-member-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        db.Users.Add(member);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var session = await environment.LoginAsync(member.Handle);
        var (csrfToken, antiforgeryCookie) = await environment.ApiFactory.GetCsrfAsync(session);
        var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        var oversizedTitle = $"超大请求体帖子 {suffix}";
        using (var oversized = await PostPostAsync(
                   client,
                   new
                   {
                       title = oversizedTitle,
                       categoryId = category.Id,
                       bodyMd = new string('超', 3000)
                   },
                   session,
                   csrfToken,
                   antiforgeryCookie))
        {
            Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
            using var document = JsonDocument.Parse(await oversized.Content.ReadAsStringAsync());
            Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());
            var details = document.RootElement.GetProperty("details");
            Assert.Equal("body", details.GetProperty("field").GetString());
            Assert.Equal("body_too_large", details.GetProperty("reason").GetString());
            Assert.Equal(2048, details.GetProperty("maxBytes").GetInt64());
        }

        Assert.False(await db.Posts.AnyAsync(post => post.Title == oversizedTitle));
        Assert.False(await db.AuditLogs.AnyAsync(log => log.ActorId == member.Id));

        var chunkedTitle = $"分块超大请求体帖子 {suffix}";
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                title = chunkedTitle,
                categoryId = category.Id,
                bodyMd = new string('块', 3000)
            },
            JsonOptions);
        using (var content = new StreamContent(new NonSeekableReadStream(payload)))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts")
            {
                Content = content
            };
            request.Headers.Add("Cookie", $"{session}; {antiforgeryCookie}");
            request.Headers.Add("X-CSRF-Token", csrfToken);
            Assert.Null(request.Content.Headers.ContentLength);
            using var chunked = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, chunked.StatusCode);
            using var document = JsonDocument.Parse(await chunked.Content.ReadAsStringAsync());
            Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "body_too_large",
                document.RootElement.GetProperty("details").GetProperty("reason").GetString());
        }

        Assert.False(await db.Posts.AnyAsync(post => post.Title == chunkedTitle));
    }

    private static async Task<HttpResponseMessage> PostPostAsync(
        HttpClient client,
        object body,
        string sessionCookie,
        string csrfToken,
        string antiforgeryCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Cookie", $"{sessionCookie}; {antiforgeryCookie}");
        request.Headers.Add("X-CSRF-Token", csrfToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string sessionCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", sessionCookie);
        return await client.SendAsync(request);
    }

    private sealed class NonSeekableReadStream(byte[] payload) : MemoryStream(payload)
    {
        public override bool CanSeek => false;
    }
}