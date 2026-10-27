using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum ServiceAccountOutcome
{
    Success,
    Unchanged,
    NotFound,
    Forbidden,
    InvalidInput,
    DuplicateName,
    AccountDisabled
}

public enum ServiceTokenValidationStatus
{
    Valid,
    Invalid,
    Expired,
    Revoked,
    AccountDisabled,
    ScopeDenied,
    IpDenied
}

public sealed record ServiceAccountListItem(
    Guid Id,
    string Name,
    string OwnerHandle,
    string Status,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> IpAllowlist,
    int? DailyTokenBudget,
    int TotalTokens,
    int ActiveTokens,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? DisabledAt);

public sealed record ServiceTokenListItem(
    Guid Id,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

public sealed record ServiceAccountDetail(
    ServiceAccountListItem Account,
    IReadOnlyList<ServiceTokenListItem> Tokens);

public sealed record ServiceAccountCreateResult(ServiceAccountOutcome Outcome, Guid? AccountId);

public sealed record ServiceTokenCreationData(
    Guid TokenId,
    Guid AccountId,
    string AccountName,
    string PlaintextToken,
    string Prefix,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record ServiceTokenCreateResult(
    ServiceAccountOutcome Outcome,
    ServiceTokenCreationData? Token);

public sealed record ServiceTokenIdentity(
    ServiceTokenValidationStatus Status,
    Guid? ServiceAccountId,
    string? ServiceAccountName,
    IReadOnlyList<string> Scopes);

public sealed class ServiceAccountAdminService(
    WhalePodDbContext db,
    AdminAccessService adminAccess,
    TimeProvider timeProvider)
{
    public static readonly string[] AllowedScopes =
    [
        "moderation:read",
        "moderation:suggest",
        "agent:heartbeat"
    ];

    public async Task<IReadOnlyList<ServiceAccountListItem>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var accounts = await (
                from account in db.ServiceAccounts.AsNoTracking()
                join owner in db.Users.AsNoTracking() on account.OwnerId equals owner.Id
                orderby account.CreatedAt descending, account.Id descending
                select new { Account = account, OwnerHandle = owner.Handle })
            .ToListAsync(cancellationToken);

        var counts = await db.ServiceTokens
            .AsNoTracking()
            .GroupBy(token => token.ServiceAccountId)
            .Select(group => new
            {
                AccountId = group.Key,
                Total = group.Count(),
                Active = group.Count(token => token.RevokedAt == null && token.ExpiresAt > now)
            })
            .ToDictionaryAsync(item => item.AccountId, cancellationToken);

        return accounts
            .Select(row =>
            {
                var count = counts.GetValueOrDefault(row.Account.Id);
                return ToListItem(
                    row.Account,
                    row.OwnerHandle,
                    count?.Total ?? 0,
                    count?.Active ?? 0);
            })
            .ToList();
    }

    public async Task<ServiceAccountDetail?> GetDetailAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var row = await (
                from account in db.ServiceAccounts.AsNoTracking()
                join owner in db.Users.AsNoTracking() on account.OwnerId equals owner.Id
                where account.Id == accountId
                select new { Account = account, OwnerHandle = owner.Handle })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var tokenRows = await db.ServiceTokens
            .AsNoTracking()
            .Where(token => token.ServiceAccountId == accountId)
            .OrderByDescending(token => token.CreatedAt)
            .ThenByDescending(token => token.Id)
            .Select(token => new
            {
                token.Id,
                token.Prefix,
                token.CreatedAt,
                token.ExpiresAt,
                token.RevokedAt,
                token.LastUsedAt
            })
            .ToListAsync(cancellationToken);

        var tokens = tokenRows
            .Select(token => new ServiceTokenListItem(
                token.Id,
                token.Prefix,
                token.CreatedAt,
                token.ExpiresAt,
                token.RevokedAt,
                token.LastUsedAt))
            .ToList();

        var now = timeProvider.GetUtcNow();
        var activeTokens = tokens.Count(token => token.RevokedAt is null && token.ExpiresAt > now);
        return new ServiceAccountDetail(
            ToListItem(row.Account, row.OwnerHandle, tokens.Count, activeTokens),
            tokens);
    }

    public async Task<ServiceAccountOutcome> SetDailyTokenBudgetAsync(
        Guid accountId,
        int? dailyTokenBudget,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return ServiceAccountOutcome.Forbidden;
        }

        if (dailyTokenBudget is { } budget
            && (budget < 1 || budget > AiSettingsService.MaxDailyTokenBudget))
        {
            return ServiceAccountOutcome.InvalidInput;
        }

        var account = await db.ServiceAccounts
            .SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return ServiceAccountOutcome.NotFound;
        }

        if (account.DailyTokenBudget == dailyTokenBudget)
        {
            return ServiceAccountOutcome.Unchanged;
        }

        var previous = account.DailyTokenBudget;
        account.DailyTokenBudget = dailyTokenBudget;
        db.AuditLogs.Add(NewAudit(
            audit,
            "service_account.budget_changed",
            account.Id,
            new { previousDailyTokenBudget = previous, dailyTokenBudget },
            timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceAccountOutcome.Success;
    }

    public async Task<ServiceAccountCreateResult> CreateAsync(
        string? name,
        IReadOnlyCollection<string>? scopes,
        IReadOnlyCollection<string>? ipAllowlist,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return new ServiceAccountCreateResult(ServiceAccountOutcome.Forbidden, null);
        }

        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length == 0
            || normalizedName.EnumerateRunes().Count() > 80
            || normalizedName.Any(char.IsControl))
        {
            return new ServiceAccountCreateResult(ServiceAccountOutcome.InvalidInput, null);
        }

        if (!TryNormalizeScopes(scopes, out var normalizedScopes)
            || !TryNormalizeIpAllowlist(ipAllowlist, out var normalizedAllowlist))
        {
            return new ServiceAccountCreateResult(ServiceAccountOutcome.InvalidInput, null);
        }

        var now = timeProvider.GetUtcNow();
        var account = new ServiceAccount
        {
            Name = normalizedName,
            OwnerId = audit.ActorId,
            Scopes = normalizedScopes,
            IpAllowlist = normalizedAllowlist,
            Status = ServiceAccountStatuses.Active,
            CreatedAt = now
        };

        db.ServiceAccounts.Add(account);
        db.AuditLogs.Add(NewAudit(
            audit,
            "service_account.created",
            account.Id,
            new
            {
                scopes = normalizedScopes,
                ipAllowlistCount = normalizedAllowlist?.Length ?? 0
            },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
        {
            SqlState: "23505",
            ConstraintName: "uq_service_account_name"
        })
        {
            db.ChangeTracker.Clear();
            return new ServiceAccountCreateResult(ServiceAccountOutcome.DuplicateName, null);
        }

        return new ServiceAccountCreateResult(ServiceAccountOutcome.Success, account.Id);
    }

    public async Task<ServiceAccountOutcome> DisableAsync(
        Guid accountId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return ServiceAccountOutcome.Forbidden;
        }

        var account = await db.ServiceAccounts
            .SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return ServiceAccountOutcome.NotFound;
        }

        if (account.Status == ServiceAccountStatuses.Disabled)
        {
            return ServiceAccountOutcome.Unchanged;
        }

        var now = timeProvider.GetUtcNow();
        account.Status = ServiceAccountStatuses.Disabled;
        account.DisabledAt = now;

        var nowActiveTokens = await db.ServiceTokens
            .Where(token => token.ServiceAccountId == accountId && token.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in nowActiveTokens)
        {
            token.RevokedAt = now;
            token.RevokedBy = audit.ActorId;
        }

        db.AuditLogs.Add(NewAudit(
            audit,
            "service_account.disabled",
            account.Id,
            new { revokedTokens = nowActiveTokens.Count },
            now));

        await db.SaveChangesAsync(cancellationToken);
        return ServiceAccountOutcome.Success;
    }

    public async Task<ServiceTokenCreateResult> IssueTokenAsync(
        Guid accountId,
        int expiresInDays,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return new ServiceTokenCreateResult(ServiceAccountOutcome.Forbidden, null);
        }

        if (expiresInDays is < 1 or > 365)
        {
            return new ServiceTokenCreateResult(ServiceAccountOutcome.InvalidInput, null);
        }

        var account = await db.ServiceAccounts
            .SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return new ServiceTokenCreateResult(ServiceAccountOutcome.NotFound, null);
        }

        if (account.Status != ServiceAccountStatuses.Active)
        {
            return new ServiceTokenCreateResult(ServiceAccountOutcome.AccountDisabled, null);
        }

        var now = timeProvider.GetUtcNow();
        var creation = CreateToken(account, now, expiresInDays);
        db.ServiceTokens.Add(creation.Token);
        db.AuditLogs.Add(NewAudit(
            audit,
            "token.issued",
            account.Id,
            new { prefix = creation.Data.Prefix, expiresAt = creation.Data.ExpiresAt },
            now));

        await db.SaveChangesAsync(cancellationToken);
        return new ServiceTokenCreateResult(ServiceAccountOutcome.Success, creation.Data);
    }

    public async Task<ServiceTokenCreateResult> RotateTokenAsync(
        Guid accountId,
        int expiresInDays,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return new ServiceTokenCreateResult(ServiceAccountOutcome.Forbidden, null);
        }

        if (expiresInDays is < 1 or > 365)
        {
            return new ServiceTokenCreateResult(ServiceAccountOutcome.InvalidInput, null);
        }

        var account = await db.ServiceAccounts
            .SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return new ServiceTokenCreateResult(ServiceAccountOutcome.NotFound, null);
        }

        if (account.Status != ServiceAccountStatuses.Active)
        {
            return new ServiceTokenCreateResult(ServiceAccountOutcome.AccountDisabled, null);
        }

        var now = timeProvider.GetUtcNow();
        var activeTokens = await db.ServiceTokens
            .Where(token => token.ServiceAccountId == accountId && token.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.RevokedAt = now;
            token.RevokedBy = audit.ActorId;
        }

        var creation = CreateToken(account, now, expiresInDays);
        db.ServiceTokens.Add(creation.Token);
        db.AuditLogs.Add(NewAudit(
            audit,
            "token.rotated",
            account.Id,
            new
            {
                revokedTokens = activeTokens.Count,
                prefix = creation.Data.Prefix,
                expiresAt = creation.Data.ExpiresAt
            },
            now));

        await db.SaveChangesAsync(cancellationToken);
        return new ServiceTokenCreateResult(ServiceAccountOutcome.Success, creation.Data);
    }

    public async Task<ServiceAccountOutcome> RevokeTokenAsync(
        Guid accountId,
        Guid tokenId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return ServiceAccountOutcome.Forbidden;
        }

        var token = await db.ServiceTokens
            .SingleOrDefaultAsync(
                candidate => candidate.Id == tokenId && candidate.ServiceAccountId == accountId,
                cancellationToken);

        if (token is null)
        {
            return ServiceAccountOutcome.NotFound;
        }

        if (token.RevokedAt is not null)
        {
            return ServiceAccountOutcome.Unchanged;
        }

        var now = timeProvider.GetUtcNow();
        token.RevokedAt = now;
        token.RevokedBy = audit.ActorId;

        db.AuditLogs.Add(NewAudit(
            audit,
            "token.revoked",
            accountId,
            new { prefix = token.Prefix, tokenId },
            now));

        await db.SaveChangesAsync(cancellationToken);
        return ServiceAccountOutcome.Success;
    }

    public async Task<ServiceTokenIdentity> AuthenticateTokenAsync(
        string? token,
        string? remoteIp,
        CancellationToken cancellationToken = default)
    {
        var normalizedToken = (token ?? string.Empty).Trim();
        if (normalizedToken.Length is < 20 or > 200)
        {
            return new ServiceTokenIdentity(
                ServiceTokenValidationStatus.Invalid,
                null,
                null,
                []);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedToken));
        var row = await (
                from candidate in db.ServiceTokens.AsNoTracking()
                join account in db.ServiceAccounts.AsNoTracking()
                    on candidate.ServiceAccountId equals account.Id
                where candidate.TokenHash == hash
                select new { Token = candidate, Account = account })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return new ServiceTokenIdentity(
                ServiceTokenValidationStatus.Invalid,
                null,
                null,
                []);
        }

        var status = EvaluateToken(row.Token, row.Account, null, remoteIp, timeProvider.GetUtcNow());
        var identity = new ServiceTokenIdentity(
            status,
            row.Account.Id,
            row.Account.Name,
            row.Account.Scopes);
        if (status != ServiceTokenValidationStatus.Valid)
        {
            return identity;
        }

        var now = timeProvider.GetUtcNow();
        await db.ServiceTokens
            .Where(candidate => candidate.Id == row.Token.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.LastUsedAt, now),
                cancellationToken);
        await db.ServiceAccounts
            .Where(candidate => candidate.Id == row.Account.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.LastUsedAt, now),
                cancellationToken);
        return identity;
    }

    public async Task<ServiceTokenValidationStatus> ValidateTokenAsync(
        string? token,
        string requiredScope,
        string? remoteIp,
        CancellationToken cancellationToken = default)
    {
        var normalizedToken = (token ?? string.Empty).Trim();
        if (normalizedToken.Length is < 20 or > 200)
        {
            return ServiceTokenValidationStatus.Invalid;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedToken));
        var row = await (
                from candidate in db.ServiceTokens.AsNoTracking()
                join account in db.ServiceAccounts.AsNoTracking()
                    on candidate.ServiceAccountId equals account.Id
                where candidate.TokenHash == hash
                select new { Token = candidate, Account = account })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return ServiceTokenValidationStatus.Invalid;
        }

        return EvaluateToken(
            row.Token,
            row.Account,
            requiredScope,
            remoteIp,
            timeProvider.GetUtcNow());
    }

    private static ServiceTokenValidationStatus EvaluateToken(
        ServiceToken token,
        ServiceAccount account,
        string? requiredScope,
        string? remoteIp,
        DateTimeOffset now)
    {
        if (token.RevokedAt is not null)
        {
            return ServiceTokenValidationStatus.Revoked;
        }

        if (token.ExpiresAt <= now)
        {
            return ServiceTokenValidationStatus.Expired;
        }

        if (account.Status != ServiceAccountStatuses.Active)
        {
            return ServiceTokenValidationStatus.AccountDisabled;
        }

        if (requiredScope is not null
            && !account.Scopes.Contains(requiredScope, StringComparer.Ordinal))
        {
            return ServiceTokenValidationStatus.ScopeDenied;
        }

        if (!IsIpAllowed(account.IpAllowlist, remoteIp))
        {
            return ServiceTokenValidationStatus.IpDenied;
        }

        return ServiceTokenValidationStatus.Valid;
    }

    private static (ServiceToken Token, ServiceTokenCreationData Data) CreateToken(
        ServiceAccount account,
        DateTimeOffset now,
        int expiresInDays)
    {
        var random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var plaintext = $"wp_{random}";
        var prefix = plaintext[..12];
        var expiresAt = now.AddDays(expiresInDays);

        var token = new ServiceToken
        {
            ServiceAccountId = account.Id,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)),
            Prefix = prefix,
            ExpiresAt = expiresAt,
            CreatedAt = now
        };

        var data = new ServiceTokenCreationData(
            token.Id,
            account.Id,
            account.Name,
            plaintext,
            prefix,
            expiresAt,
            now);

        return (token, data);
    }

    private static bool TryNormalizeScopes(
        IReadOnlyCollection<string>? scopes,
        out string[] normalized)
    {
        normalized = (scopes ?? Array.Empty<string>())
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();

        return normalized.Length > 0
            && normalized.All(scope => AllowedScopes.Contains(scope, StringComparer.Ordinal));
    }

    private static bool TryNormalizeIpAllowlist(
        IReadOnlyCollection<string>? values,
        out IPAddress[]? normalized)
    {
        normalized = null;
        if (values is null || values.Count == 0)
        {
            return true;
        }

        if (values.Count > 20)
        {
            return false;
        }

        var addresses = new List<IPAddress>();
        foreach (var value in values)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Contains('/')
                || !IPAddress.TryParse(trimmed, out var address))
            {
                return false;
            }

            addresses.Add(NormalizeAddress(address));
        }

        normalized = addresses
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();

        return true;
    }

    private static bool IsIpAllowed(IPAddress[]? allowlist, string? remoteIp)
    {
        if (allowlist is null || allowlist.Length == 0)
        {
            return true;
        }

        if (!IPAddress.TryParse(remoteIp, out var address))
        {
            return false;
        }

        var normalized = NormalizeAddress(address);
        return allowlist.Any(allowed => allowed.Equals(normalized));
    }

    private static IPAddress NormalizeAddress(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static ServiceAccountListItem ToListItem(
        ServiceAccount account,
        string ownerHandle,
        int totalTokens,
        int activeTokens)
    {
        return new ServiceAccountListItem(
            account.Id,
            account.Name,
            ownerHandle,
            account.Status,
            account.Scopes,
            account.IpAllowlist?.Select(address => address.ToString()).ToList() ?? [],
            account.DailyTokenBudget,
            totalTokens,
            activeTokens,
            account.CreatedAt,
            account.LastUsedAt,
            account.DisabledAt);
    }

    private static AuditLog NewAudit(
        AuditContext audit,
        string action,
        Guid accountId,
        object metadata,
        DateTimeOffset createdAt)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = audit.ActorId,
            Action = action,
            TargetType = "service_account",
            TargetId = accountId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(metadata),
            CreatedAt = createdAt
        };
    }
}