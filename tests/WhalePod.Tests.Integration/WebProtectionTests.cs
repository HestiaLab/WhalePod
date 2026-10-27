using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;

namespace WhalePod.Tests.Integration;

public sealed class WebProtectionEnvironment : IAsyncLifetime
{
    private readonly string _keysDirectory = Path.Combine(
        Path.GetTempPath(),
        $"whalepod-web-protection-dpkeys-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _previousEnvironment = new();

    public WebProtectionEnvironment()
    {
        SetEnvironment("ConnectionStrings__WhalePod", ServiceTestSupport.ConnectionString);
        SetEnvironment("DataProtection__KeysDirectory", _keysDirectory);
        SetEnvironment("Audit__IpHashSecret", "test-only-audit-ip-hash-secret-0123456789");
        SetEnvironment("Auth__DevLoginEnabled", "true");
        SetEnvironment("WebWriteRateLimit__PermitLimit", "2");
        SetEnvironment("WebWriteRateLimit__WindowSeconds", "60");
        SetEnvironment("WebRequestLimits__MaxFormBodyBytes", "2048");
        SetEnvironment("InviteRedeemRateLimit__PermitLimit", "1");
        SetEnvironment("InviteRedeemRateLimit__WindowSeconds", "60");
        WebFactory = new WhalePodWebFactory(
            _keysDirectory,
            new Dictionary<string, string?>
            {
                ["WebWriteRateLimit:PermitLimit"] = "2",
                ["WebWriteRateLimit:WindowSeconds"] = "60",
                ["WebRequestLimits:MaxFormBodyBytes"] = "2048",
                ["InviteRedeemRateLimit:PermitLimit"] = "1",
                ["InviteRedeemRateLimit:WindowSeconds"] = "60"
            });
    }

    public WhalePodWebFactory WebFactory { get; }

    public async Task InitializeAsync()
    {
        await using var scope = WebFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WhalePod.Infrastructure.Persistence.WhalePodDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await WebFactory.DisposeAsync();
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

public sealed class WebProtectionTests(WebProtectionEnvironment environment)
    : IClassFixture<WebProtectionEnvironment>
{
    [DatabaseFact]
    public async Task Web_form_rate_limit_returns_429_with_retry_after_and_no_extra_writes()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("web-limit-member");
        member.Handle = $"web-limit-member-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(member.Id, category.Id, title: $"Web 限流帖子 {suffix}");
        db.Users.Add(member);
        db.Categories.Add(category);
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        var session = await environment.WebFactory.LoginAsync(member.Handle);
        using var client = CreateClient();
        var form = await LoadFormAsync(client, $"/posts/{post.Id}", session);

        for (var index = 0; index < 2; index++)
        {
            using var accepted = await PostFormAsync(
                client,
                $"/posts/{post.Id}/comments",
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = form.Token,
                    ["bodyMd"] = $"Web 限流允许的评论 {index} {suffix}，内容长度足够。"
                },
                session,
                form.CsrfCookie);
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        }

        var extraBody = $"Web 限流拒绝的评论 {suffix}，不得写入数据库。";
        using var limited = await PostFormAsync(
            client,
            $"/posts/{post.Id}/comments",
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = form.Token,
                ["bodyMd"] = extraBody
            },
            session,
            form.CsrfCookie);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Comments.CountAsync(comment => comment.PostId == post.Id));
        Assert.False(await db.Comments.AnyAsync(comment => comment.BodyMd == extraBody));
        Assert.Equal(
            2,
            await db.AuditLogs.CountAsync(log => log.ActorId == member.Id && log.Action == "comment.created"));
    }

    [DatabaseFact]
    public async Task Oversized_web_form_returns_400_without_business_side_effects()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("web-body-member");
        member.Handle = $"web-body-member-{suffix}";
        member.DisplayName = $"原始显示名 {suffix}";
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var session = await environment.WebFactory.LoginAsync(member.Handle);
        using var client = CreateClient();
        var form = await LoadFormAsync(client, "/settings/profile", session);

        using var response = await PostFormAsync(
            client,
            "/settings/profile/update",
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = form.Token,
                ["displayName"] = new string('名', 1200),
                ["bio"] = "超过表单大小上限的请求不应进入业务服务。"
            },
            session,
            form.CsrfCookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        db.ChangeTracker.Clear();
        Assert.Equal($"原始显示名 {suffix}", await db.Users
            .Where(user => user.Id == member.Id)
            .Select(user => user.DisplayName)
            .SingleAsync());
        Assert.False(await db.AuditLogs.AnyAsync(log =>
            log.ActorId == member.Id && log.Action == "profile.updated"));
    }

    [DatabaseFact]
    public async Task Invite_redemption_is_rate_limited_by_account_and_address()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("web-redeem-member");
        member.Handle = $"web-redeem-member-{suffix}";
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var session = await environment.WebFactory.LoginAsync(member.Handle);
        using var client = CreateClient();
        var form = await LoadFormAsync(client, "/activate", session);

        using var first = await PostFormAsync(
            client,
            "/activate/redeem",
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = form.Token,
                ["code"] = $"INVALID-{suffix}"
            },
            session,
            form.CsrfCookie);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        using var limited = await PostFormAsync(
            client,
            "/activate/redeem",
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = form.Token,
                ["code"] = $"INVALID-{suffix}"
            },
            session,
            form.CsrfCookie);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
    }

    [DatabaseFact]
    public async Task Web_and_api_do_not_emit_wildcard_cors_headers()
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", "https://evil.example");
        using var response = await client.SendAsync(request);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    private HttpClient CreateClient()
    {
        return environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    private static async Task<FormContext> LoadFormAsync(
        HttpClient client,
        string path,
        string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", sessionCookie);
        using var response = await client.SendAsync(request);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode} ({response.StatusCode}) Location={response.Headers.Location}.");
        var html = await response.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, $"Antiforgery token missing on {path}.");
        var csrf = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(value => value.Split(';', 2)[0])
                .FirstOrDefault(value => value.StartsWith("whalepod_csrf=", StringComparison.Ordinal))
            : null;
        Assert.NotNull(csrf);
        return new FormContext(WebUtility.HtmlDecode(tokenMatch.Groups[1].Value), csrf!);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> form,
        string sessionCookie,
        string csrfCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        return await client.SendAsync(request);
    }

    private sealed record FormContext(string Token, string CsrfCookie);
}