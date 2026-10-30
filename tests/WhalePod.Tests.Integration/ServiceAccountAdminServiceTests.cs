using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class ServiceAccountAdminServiceTests
{
    private static WhalePodDbContext CreateContext() => ServiceTestSupport.CreateContext();

    [DatabaseFact]
    public async Task Create_account_validates_scopes_ips_and_audits()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("svc-admin", UserRoles.Admin);
        var member = NewUser("svc-member", UserRoles.Member);
        db.Users.AddRange(admin, member);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var name = $"CI Agent {Guid.NewGuid():N}";
        var result = await service.CreateAsync(
            $"  {name}  ",
            ["agent:heartbeat", "moderation:read"],
            ["127.0.0.1", "::1"],
            NewAudit(admin.Id));

        Assert.Equal(ServiceAccountOutcome.Success, result.Outcome);
        var accountId = Assert.IsType<Guid>(result.AccountId);

        db.ChangeTracker.Clear();
        var account = await db.ServiceAccounts.SingleAsync(candidate => candidate.Id == accountId);
        Assert.Equal(name, account.Name);
        Assert.Equal(admin.Id, account.OwnerId);
        Assert.Equal(new[] { "agent:heartbeat", "moderation:read" }, account.Scopes);
        Assert.Contains(IPAddress.Parse("127.0.0.1"), account.IpAllowlist!);
        Assert.Contains(IPAddress.Parse("::1"), account.IpAllowlist!);

        var audit = await db.AuditLogs.SingleAsync(log =>
            log.TargetId == accountId && log.Action == "service_account.created");
        Assert.Equal(32, audit.IpHash?.Length);

        var duplicate = await CreateService(db).CreateAsync(
            name,
            ["agent:heartbeat"],
            null,
            NewAudit(admin.Id));
        Assert.Equal(ServiceAccountOutcome.DuplicateName, duplicate.Outcome);

        var invalidScope = await CreateService(db).CreateAsync(
            $"{name}-bad-scope",
            ["admin:all"],
            null,
            NewAudit(admin.Id));
        Assert.Equal(ServiceAccountOutcome.InvalidInput, invalidScope.Outcome);

        var invalidIp = await CreateService(db).CreateAsync(
            $"{name}-bad-ip",
            ["agent:heartbeat"],
            ["10.0.0.0/8"],
            NewAudit(admin.Id));
        Assert.Equal(ServiceAccountOutcome.InvalidInput, invalidIp.Outcome);

        var forbidden = await CreateService(db).CreateAsync(
            $"{name}-forbidden",
            ["agent:heartbeat"],
            null,
            NewAudit(member.Id));
        Assert.Equal(ServiceAccountOutcome.Forbidden, forbidden.Outcome);
    }

    [DatabaseFact]
    public async Task Issue_token_returns_once_and_validates_scope_expiry_and_ip()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("token-admin", UserRoles.Admin);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var accountId = await CreateAccountAsync(
            service,
            admin.Id,
            ["agent:heartbeat"],
            ["127.0.0.1"]);

        var result = await service.IssueTokenAsync(accountId, 90, NewAudit(admin.Id));
        Assert.Equal(ServiceAccountOutcome.Success, result.Outcome);
        var token = Assert.IsType<ServiceTokenCreationData>(result.Token);
        Assert.StartsWith("wp_", token.PlaintextToken);
        Assert.Equal(12, token.Prefix.Length);
        Assert.Equal(token.PlaintextToken[..12], token.Prefix);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow.AddDays(89));

        db.ChangeTracker.Clear();
        var stored = await db.ServiceTokens.SingleAsync(candidate => candidate.Id == token.TokenId);
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(token.PlaintextToken)),
            stored.TokenHash);
        Assert.True(stored.ExpiresAt > stored.CreatedAt);

        Assert.Equal(
            ServiceTokenValidationStatus.Valid,
            await service.ValidateTokenAsync(token.PlaintextToken, "agent:heartbeat", "127.0.0.1"));
        Assert.Equal(
            ServiceTokenValidationStatus.IpDenied,
            await service.ValidateTokenAsync(token.PlaintextToken, "agent:heartbeat", "10.0.0.1"));
        Assert.Equal(
            ServiceTokenValidationStatus.ScopeDenied,
            await service.ValidateTokenAsync(token.PlaintextToken, "moderation:read", "127.0.0.1"));
        Assert.Equal(
            ServiceTokenValidationStatus.Invalid,
            await service.ValidateTokenAsync("wp_not-a-real-token", "agent:heartbeat", "127.0.0.1"));

        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.TargetId == accountId && log.Action == "token.issued"));
    }

    [DatabaseFact]
    public async Task Revoke_and_rotate_invalidate_old_tokens()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("token-rotate", UserRoles.Admin);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var accountId = await CreateAccountAsync(service, admin.Id, ["moderation:suggest"], null);

        var issued = Assert.IsType<ServiceTokenCreationData>(
            (await service.IssueTokenAsync(accountId, 30, NewAudit(admin.Id))).Token);
        Assert.Equal(
            ServiceAccountOutcome.Success,
            await service.RevokeTokenAsync(accountId, issued.TokenId, NewAudit(admin.Id)));
        Assert.Equal(
            ServiceTokenValidationStatus.Revoked,
            await service.ValidateTokenAsync(issued.PlaintextToken, "moderation:suggest", null));
        Assert.Equal(
            ServiceAccountOutcome.Unchanged,
            await service.RevokeTokenAsync(accountId, issued.TokenId, NewAudit(admin.Id)));

        var rotated = await service.RotateTokenAsync(accountId, 30, NewAudit(admin.Id));
        Assert.Equal(ServiceAccountOutcome.Success, rotated.Outcome);
        var newToken = Assert.IsType<ServiceTokenCreationData>(rotated.Token);
        Assert.Equal(
            ServiceTokenValidationStatus.Valid,
            await service.ValidateTokenAsync(newToken.PlaintextToken, "moderation:suggest", null));

        db.ChangeTracker.Clear();
        var tokens = await db.ServiceTokens
            .Where(token => token.ServiceAccountId == accountId)
            .ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens.Where(token => token.Id != newToken.TokenId), token => Assert.NotNull(token.RevokedAt));
        Assert.Null(tokens.Single(token => token.Id == newToken.TokenId).RevokedAt);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.TargetId == accountId && log.Action == "token.rotated"));
    }

    [DatabaseFact]
    public async Task Disable_account_revokes_tokens_and_blocks_new_tokens()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("token-disable", UserRoles.Admin);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var accountId = await CreateAccountAsync(service, admin.Id, ["agent:heartbeat"], null);
        var issued = Assert.IsType<ServiceTokenCreationData>(
            (await service.IssueTokenAsync(accountId, 90, NewAudit(admin.Id))).Token);

        Assert.Equal(
            ServiceAccountOutcome.Success,
            await service.DisableAsync(accountId, NewAudit(admin.Id)));
        Assert.Equal(
            ServiceTokenValidationStatus.Revoked,
            await service.ValidateTokenAsync(issued.PlaintextToken, "agent:heartbeat", null));
        Assert.Equal(
            ServiceAccountOutcome.AccountDisabled,
            (await service.IssueTokenAsync(accountId, 90, NewAudit(admin.Id))).Outcome);
        Assert.Equal(
            ServiceAccountOutcome.AccountDisabled,
            (await service.RotateTokenAsync(accountId, 90, NewAudit(admin.Id))).Outcome);

        db.ChangeTracker.Clear();
        var account = await db.ServiceAccounts.SingleAsync(candidate => candidate.Id == accountId);
        Assert.Equal(ServiceAccountStatuses.Disabled, account.Status);
        Assert.NotNull(account.DisabledAt);
        Assert.NotNull((await db.ServiceTokens.SingleAsync(token => token.Id == issued.TokenId)).RevokedAt);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.TargetId == accountId && log.Action == "service_account.disabled"));
    }

    [DatabaseFact]
    public async Task Daily_token_budget_override_is_validated_audited_and_clearable()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        var admin = NewUser("budget-admin", UserRoles.Admin);
        var member = NewUser("budget-member", UserRoles.Member);
        db.Users.AddRange(admin, member);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var accountId = await CreateAccountAsync(service, admin.Id, ["moderation:suggest"], null);

        Assert.Equal(
            ServiceAccountOutcome.Success,
            await service.SetDailyTokenBudgetAsync(accountId, 50_000, NewAudit(admin.Id)));
        db.ChangeTracker.Clear();
        Assert.Equal(
            50_000,
            (await db.ServiceAccounts.SingleAsync(account => account.Id == accountId)).DailyTokenBudget);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "service_account.budget_changed" && log.TargetId == accountId));

        Assert.Equal(
            ServiceAccountOutcome.Unchanged,
            await service.SetDailyTokenBudgetAsync(accountId, 50_000, NewAudit(admin.Id)));
        Assert.Equal(
            ServiceAccountOutcome.InvalidInput,
            await service.SetDailyTokenBudgetAsync(accountId, 0, NewAudit(admin.Id)));
        Assert.Equal(
            ServiceAccountOutcome.Forbidden,
            await service.SetDailyTokenBudgetAsync(accountId, 1_000, NewAudit(member.Id)));
        Assert.Equal(
            ServiceAccountOutcome.NotFound,
            await service.SetDailyTokenBudgetAsync(Guid.NewGuid(), 1_000, NewAudit(admin.Id)));

        Assert.Equal(
            ServiceAccountOutcome.Success,
            await service.SetDailyTokenBudgetAsync(accountId, null, NewAudit(admin.Id)));
        db.ChangeTracker.Clear();
        Assert.Null(
            (await db.ServiceAccounts.SingleAsync(account => account.Id == accountId)).DailyTokenBudget);
    }

    private static ServiceAccountAdminService CreateService(WhalePodDbContext db)
    {
        return new ServiceAccountAdminService(db, new AdminAccessService(db), TimeProvider.System);
    }

    private static async Task<Guid> CreateAccountAsync(
        ServiceAccountAdminService service,
        Guid adminId,
        string[] scopes,
        string[]? ips)
    {
        var result = await service.CreateAsync(
            $"Agent {Guid.NewGuid():N}",
            scopes,
            ips,
            NewAudit(adminId));
        Assert.Equal(ServiceAccountOutcome.Success, result.Outcome);
        return Assert.IsType<Guid>(result.AccountId);
    }

    private static AuditContext NewAudit(Guid actorId)
    {
        return new AuditContext(actorId, "test-request", new byte[32]);
    }

    private static User NewUser(string prefix, string role)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new User
        {
            Handle = $"{prefix}-{suffix}",
            DisplayName = "测试用户",
            Status = UserStatuses.Active,
            Role = role,
            ActivatedAt = DateTimeOffset.UtcNow
        };
    }
}