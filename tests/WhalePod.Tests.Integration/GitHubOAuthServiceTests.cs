using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;
using WhalePod.Web.Services;

namespace WhalePod.Tests.Integration;

public sealed class GitHubOAuthServiceTests
{
    [DatabaseFact]
    public async Task OAuth_profile_creates_pending_github_user_and_is_idempotent()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var login = $"oauth-{suffix}";
        var githubId = Random.Shared.NextInt64(10_000_000, long.MaxValue);
        var service = CreateService(db, new FakeGitHubHandler(login, githubId));

        var token = await service.ExchangeCodeAsync("code", CancellationToken.None);
        Assert.Equal("test-token", token);
        var profile = await service.GetProfileAsync(token!, CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal(login, profile!.Login);
        Assert.Equal("oauth@example.test", profile.Email);
        Assert.True(profile.EmailVerified);

        var first = await service.SignInAsync(
            profile,
            userId => new AuditContext(userId, "oauth-test", new byte[32]),
            CancellationToken.None);
        Assert.Equal(GitHubSignInOutcome.Success, first.Outcome);
        Assert.True(first.Created);
        Assert.NotNull(first.User);
        Assert.Null(first.User!.ActivatedAt);
        Assert.Equal(profile.Id, first.User.GitHubId);
        Assert.Equal("https://avatars.githubusercontent.com/u/777?v=4", first.User.AvatarUrl);
        Assert.Equal(profile.Email, first.User.Email);
        Assert.True(first.User.EmailVerified);

        db.ChangeTracker.Clear();
        var stored = await db.Users.SingleAsync(user => user.Handle == login);
        Assert.True(await db.ExternalLogins.AnyAsync(external =>
            external.UserId == stored.Id
            && external.Provider == "github"
            && external.ProviderUserId == profile.Id.ToString()));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "auth.github_login" && log.ActorId == stored.Id));

        var second = await service.SignInAsync(
            profile,
            userId => new AuditContext(userId, "oauth-test", new byte[32]),
            CancellationToken.None);
        Assert.Equal(GitHubSignInOutcome.Success, second.Outcome);
        Assert.False(second.Created);
        Assert.Equal(stored.Id, second.User!.Id);
        Assert.Equal(
            1,
            await db.Users.CountAsync(user => user.GitHubId == profile.Id));
    }

    [DatabaseFact]
    public async Task OAuth_preserves_custom_profile_and_blocks_banned_or_disallowed()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var login = $"oauth-existing-{suffix}";
        var githubId = Random.Shared.NextInt64(10_000_000, long.MaxValue);
        var user = ServiceTestSupport.NewUser("oauth-existing");
        user.Handle = login;
        user.GitHubId = githubId;
        user.DisplayName = "自定义显示名";
        user.GitHubAvatarUrl = "https://avatars.githubusercontent.com/u/1?v=4";
        user.AvatarUrl = "/media/custom-avatar.webp";
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeGitHubHandler(login, githubId));
        var profile = await service.GetProfileAsync("test-token", CancellationToken.None);
        Assert.NotNull(profile);
        var result = await service.SignInAsync(
            profile!,
            userId => new AuditContext(userId, "oauth-test", new byte[32]),
            CancellationToken.None);
        Assert.Equal(GitHubSignInOutcome.Success, result.Outcome);
        db.ChangeTracker.Clear();
        var updated = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        Assert.Equal("自定义显示名", updated.DisplayName);
        Assert.Equal("/media/custom-avatar.webp", updated.AvatarUrl);
        Assert.Equal("https://avatars.githubusercontent.com/u/777?v=4", updated.GitHubAvatarUrl);

        updated.Status = UserStatuses.Banned;
        updated.StatusReason = "OAuth 封禁测试";
        await db.SaveChangesAsync();
        var banned = await service.SignInAsync(
            profile,
            userId => new AuditContext(userId, "oauth-test", new byte[32]),
            CancellationToken.None);
        Assert.Equal(GitHubSignInOutcome.AccountBanned, banned.Outcome);

        var restrictedOptions = Options.Create(new GitHubOAuthOptions
        {
            ClientId = "client",
            ClientSecret = "secret",
            AllowedLogins = ["someone-else"]
        });
        var restricted = new GitHubOAuthService(
            db,
            new HttpClient(new FakeGitHubHandler(login, githubId)),
            restrictedOptions,
            TimeProvider.System,
            NullLogger<GitHubOAuthService>.Instance);
        var notAllowed = await restricted.SignInAsync(
            profile,
            userId => new AuditContext(userId, "oauth-test", new byte[32]),
            CancellationToken.None);
        Assert.Equal(GitHubSignInOutcome.NotAllowed, notAllowed.Outcome);
    }

    [Fact]
    public void Authorize_url_and_return_url_validation_are_safe()
    {
        var service = new GitHubOAuthService(
            null!,
            new HttpClient(),
            Options.Create(new GitHubOAuthOptions
            {
                ClientId = "client",
                ClientSecret = "secret"
            }),
            TimeProvider.System,
            NullLogger<GitHubOAuthService>.Instance);
        var url = service.BuildAuthorizeUrl("https://localhost/auth/github/callback", "state-value");
        Assert.Contains("client_id=client", url, StringComparison.Ordinal);
        Assert.Contains("scope=read%3Auser%20user%3Aemail", url, StringComparison.Ordinal);
        Assert.Contains("state=state-value", url, StringComparison.Ordinal);

        Assert.True(GitHubOAuthService.IsSafeReturnUrl("/posts/abc"));
        Assert.True(GitHubOAuthService.IsSafeReturnUrl("/activate"));
        Assert.False(GitHubOAuthService.IsSafeReturnUrl("//evil.example"));
        Assert.False(GitHubOAuthService.IsSafeReturnUrl("https://evil.example"));
        Assert.False(GitHubOAuthService.IsSafeReturnUrl("/auth/github/start"));
        Assert.False(GitHubOAuthService.IsSafeReturnUrl("/\\evil"));
    }

    private static GitHubOAuthService CreateService(WhalePodDbContext db, HttpMessageHandler handler)
    {
        return new GitHubOAuthService(
            db,
            new HttpClient(handler),
            Options.Create(new GitHubOAuthOptions
            {
                ClientId = "client",
                ClientSecret = "secret"
            }),
            TimeProvider.System,
            NullLogger<GitHubOAuthService>.Instance);
    }

    private sealed class FakeGitHubHandler(string login, long githubId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(path switch
            {
                "/login/oauth/access_token" => Json("""{"access_token":"test-token"}"""),
                "/user" => Json(
                    $$"""{"id":{{githubId}},"login":"{{login}}","name":"OAuth User","avatar_url":"https://avatars.githubusercontent.com/u/777?v=4","email":null}"""),
                "/user/emails" => Json(
                    """[{"email":"oauth@example.test","primary":true,"verified":true}]"""),
                "/user/orgs" => Json("""[{"login":"whalepod-org"}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }

        private static HttpResponseMessage Json(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}