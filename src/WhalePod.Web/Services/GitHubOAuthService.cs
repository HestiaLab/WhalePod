using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Web.Services;

public sealed class GitHubOAuthOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizeEndpoint { get; set; } = "https://github.com/login/oauth/authorize";
    public string TokenEndpoint { get; set; } = "https://github.com/login/oauth/access_token";
    public string UserEndpoint { get; set; } = "https://api.github.com/user";
    public string EmailEndpoint { get; set; } = "https://api.github.com/user/emails";
    public string OrganizationsEndpoint { get; set; } = "https://api.github.com/user/orgs";
    public string[] AllowedLogins { get; set; } = [];
    public string[] AllowedOrganizations { get; set; } = [];
    public string CallbackPath { get; set; } = "/auth/github/callback";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed record GitHubProfile(
    long Id,
    string Login,
    string? DisplayName,
    string? AvatarUrl,
    string? Email,
    bool EmailVerified,
    IReadOnlyList<string> Organizations);

public enum GitHubSignInOutcome
{
    Success,
    NotAllowed,
    AccountBanned,
    AccountDeleted,
    AccountInactive
}

public sealed record GitHubSignInResult(
    GitHubSignInOutcome Outcome,
    User? User,
    bool Created);

public sealed record GitHubStatePayload(string State, string ReturnUrl, DateTimeOffset IssuedAt);

public sealed class GitHubOAuthStateProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("WhalePod.GitHubOAuth.State.v1");

    public string Protect(string state, string returnUrl, DateTimeOffset now)
    {
        var payload = JsonSerializer.Serialize(new GitHubStatePayload(state, returnUrl, now));
        return _protector.Protect(payload);
    }

    public GitHubStatePayload? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<GitHubStatePayload>(_protector.Unprotect(value));
            return payload is null
                || string.IsNullOrWhiteSpace(payload.State)
                || payload.IssuedAt < DateTimeOffset.UtcNow.AddMinutes(-10)
                ? null
                : payload;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }
}

public sealed class GitHubOAuthService(
    WhalePodDbContext db,
    HttpClient httpClient,
    Microsoft.Extensions.Options.IOptions<GitHubOAuthOptions> options,
    TimeProvider timeProvider,
    ILogger<GitHubOAuthService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GitHubOAuthOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public static string NewState()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool IsSafeReturnUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.StartsWith('/')
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.StartsWith("/auth/", StringComparison.Ordinal)
            && !value.StartsWith("/login", StringComparison.Ordinal)
            && !value.Contains('\\')
            && !value.Any(char.IsControl);
    }

    public string BuildAuthorizeUrl(string redirectUri, string state)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "read:user user:email",
            ["state"] = state
        };
        var queryString = string.Join(
            "&",
            query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));
        return $"{_options.AuthorizeEndpoint}?{queryString}";
    }

    public async Task<string?> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code"] = code
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("GitHub token exchange failed with status {Status}.", response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("access_token", out var token)
            && token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;
    }

    public async Task<GitHubProfile?> GetProfileAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var userRequest = new HttpRequestMessage(HttpMethod.Get, _options.UserEndpoint);
        userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        userRequest.Headers.UserAgent.ParseAdd("WhalePod/1.0");
        using var userResponse = await httpClient.SendAsync(userRequest, cancellationToken);
        if (!userResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("GitHub user request failed with status {Status}.", userResponse.StatusCode);
            return null;
        }

        using var userDocument = JsonDocument.Parse(
            await userResponse.Content.ReadAsStringAsync(cancellationToken));
        var user = userDocument.RootElement;
        if (!user.TryGetProperty("id", out var idElement)
            || !idElement.TryGetInt64(out var id)
            || !user.TryGetProperty("login", out var loginElement)
            || loginElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var login = loginElement.GetString() ?? string.Empty;
        var displayName = ReadString(user, "name");
        var avatarUrl = ReadString(user, "avatar_url");
        var profileEmail = ReadString(user, "email");
        var (email, emailVerified) = await LoadPrimaryEmailAsync(
            accessToken,
            profileEmail,
            cancellationToken);
        var organizations = _options.AllowedOrganizations.Length == 0
            ? []
            : await LoadOrganizationsAsync(accessToken, cancellationToken);
        return new GitHubProfile(
            id,
            login,
            displayName,
            avatarUrl,
            email,
            emailVerified,
            organizations);
    }

    public async Task<GitHubSignInResult> SignInAsync(
        GitHubProfile profile,
        Func<Guid, AuditContext> auditFactory,
        CancellationToken cancellationToken)
    {
        if (!IsAllowed(profile))
        {
            return new GitHubSignInResult(GitHubSignInOutcome.NotAllowed, null, false);
        }

        var providerUserId = profile.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var external = await db.ExternalLogins
            .SingleOrDefaultAsync(
                login => login.Provider == "github" && login.ProviderUserId == providerUserId,
                cancellationToken);
        User? user = null;
        if (external is not null)
        {
            user = await db.Users.SingleOrDefaultAsync(
                candidate => candidate.Id == external.UserId,
                cancellationToken);
        }
        else
        {
            user = await db.Users.SingleOrDefaultAsync(
                candidate => candidate.GitHubId == profile.Id,
                cancellationToken);
        }

        var created = false;
        if (user is null)
        {
            user = new User
            {
                Handle = await BuildUniqueHandleAsync(profile.Login, cancellationToken),
                DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName)
                    ? profile.Login
                    : TrimTo(profile.DisplayName, 80),
                GitHubId = profile.Id,
                GitHubAvatarUrl = profile.AvatarUrl,
                AvatarUrl = profile.AvatarUrl,
                Email = profile.Email,
                EmailVerified = profile.EmailVerified,
                Status = UserStatuses.Active,
                CreatedAt = timeProvider.GetUtcNow(),
                UpdatedAt = timeProvider.GetUtcNow()
            };
            db.Users.Add(user);
            created = true;
        }

        if (user.DeletedAt is not null || user.Status == UserStatuses.Deleted)
        {
            return new GitHubSignInResult(GitHubSignInOutcome.AccountDeleted, user, created);
        }

        if (user.Status == UserStatuses.Banned)
        {
            return new GitHubSignInResult(GitHubSignInOutcome.AccountBanned, user, created);
        }

        if (user.Status == UserStatuses.Muted
            && user.StatusExpiresAt is not null
            && user.StatusExpiresAt <= timeProvider.GetUtcNow())
        {
            user.Status = UserStatuses.Active;
            user.StatusReason = null;
            user.StatusExpiresAt = null;
        }

        if (user.Status is not (UserStatuses.Active or UserStatuses.Muted))
        {
            return new GitHubSignInResult(GitHubSignInOutcome.AccountInactive, user, created);
        }

        var now = timeProvider.GetUtcNow();
        if (user.GitHubId is null)
        {
            user.GitHubId = profile.Id;
        }

        var previousGitHubAvatar = user.GitHubAvatarUrl;
        user.GitHubAvatarUrl = profile.AvatarUrl;
        if (user.AvatarUrl is null
            || string.Equals(user.AvatarUrl, previousGitHubAvatar, StringComparison.Ordinal))
        {
            user.AvatarUrl = profile.AvatarUrl;
        }

        if (profile.Email is not null)
        {
            user.Email = profile.Email;
            user.EmailVerified = profile.EmailVerified;
        }

        user.DisplayName = string.IsNullOrWhiteSpace(user.DisplayName)
            ? TrimTo(profile.DisplayName ?? profile.Login, 80)
            : user.DisplayName;
        user.LastLoginAt = now;
        user.UpdatedAt = now;

        if (external is null)
        {
            external = await db.ExternalLogins.SingleOrDefaultAsync(
                login => login.UserId == user.Id && login.Provider == "github",
                cancellationToken);
        }

        if (external is null)
        {
            external = new ExternalLogin
            {
                UserId = user.Id,
                Provider = "github",
                ProviderUserId = providerUserId,
                ProviderLogin = TrimTo(profile.Login, 100),
                CreatedAt = now
            };
            db.ExternalLogins.Add(external);
        }

        external.ProviderLogin = TrimTo(profile.Login, 100);
        external.LastLoginAt = now;

        var loginAudit = auditFactory(user.Id);
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = user.Id,
            Action = "auth.github_login",
            TargetType = "user",
            TargetId = user.Id,
            RequestId = loginAudit.RequestId,
            IpHash = loginAudit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                provider = "github",
                created,
                pendingActivation = user.ActivatedAt is null
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        return new GitHubSignInResult(GitHubSignInOutcome.Success, user, created);
    }

    private bool IsAllowed(GitHubProfile profile)
    {
        if (_options.AllowedLogins.Length > 0
            && !_options.AllowedLogins.Any(value =>
                string.Equals(value, profile.Login, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_options.AllowedOrganizations.Length > 0
            && !_options.AllowedOrganizations.Any(allowed =>
                profile.Organizations.Any(actual =>
                    string.Equals(actual, allowed, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return true;
    }

    private async Task<(string? Email, bool Verified)> LoadPrimaryEmailAsync(
        string accessToken,
        string? profileEmail,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.EmailEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("WhalePod/1.0");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var emails = document.RootElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => new
                    {
                        Email = ReadString(item, "email"),
                        Primary = item.TryGetProperty("primary", out var primary)
                            && primary.ValueKind == JsonValueKind.True,
                        Verified = item.TryGetProperty("verified", out var verified)
                            && verified.ValueKind == JsonValueKind.True
                    })
                    .ToList();
                var primaryEmail = emails.FirstOrDefault(item => item.Primary)
                    ?? emails.FirstOrDefault(item => item.Verified);
                if (primaryEmail?.Email is not null)
                {
                    return (primaryEmail.Email, primaryEmail.Verified);
                }
            }
        }

        return (profileEmail, false);
    }

    private async Task<IReadOnlyList<string>> LoadOrganizationsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.OrganizationsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("WhalePod/1.0");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
                .EnumerateArray()
                .Select(item => ReadString(item, "login"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList()
            : [];
    }

    private async Task<string> BuildUniqueHandleAsync(
        string login,
        CancellationToken cancellationToken)
    {
        var baseHandle = new string(login
            .ToLowerInvariant()
            .Select(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '-')
            .ToArray())
            .Trim('-');
        if (baseHandle.Length < 3)
        {
            baseHandle = $"gh-{baseHandle}".Trim('-');
        }

        baseHandle = baseHandle.Length > 32 ? baseHandle[..32] : baseHandle;
        if (await IsHandleAvailableAsync(baseHandle, cancellationToken))
        {
            return baseHandle;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var suffix = RandomNumberGenerator.GetHexString(4, lowercase: true);
            var candidate = $"{baseHandle[..Math.Min(baseHandle.Length, 27)]}-{suffix}";
            if (await IsHandleAvailableAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("无法生成唯一 handle。");
    }

    private async Task<bool> IsHandleAvailableAsync(
        string handle,
        CancellationToken cancellationToken)
    {
        return !await db.Users.AnyAsync(user => user.Handle == handle, cancellationToken);
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string TrimTo(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}