using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class SensitiveWordAdminServiceTests
{
    private static WhalePodDbContext CreateContext() => ServiceTestSupport.CreateContext();

    [DatabaseFact]
    public async Task Create_update_and_disable_word_without_leaking_term_in_audit()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("word-admin", UserRoles.Admin);
        var member = NewUser("word-member", UserRoles.Member);
        db.Users.AddRange(admin, member);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var term = $"badword-{suffix}";

        Assert.Equal(
            SensitiveWordOutcome.Success,
            await service.CreateAsync(term, "reject", NewAudit(admin.Id)));

        db.ChangeTracker.Clear();
        var word = await db.SensitiveWords.SingleAsync(candidate => candidate.Term == term);
        Assert.Equal("reject", word.Action);
        Assert.Equal("active", word.Status);
        Assert.Equal(admin.Id, word.CreatedBy);

        var list = await CreateService(db).GetAsync();
        Assert.Contains(list.Items, item => item.Id == word.Id && item.Term == term);

        var createdAudit = await db.AuditLogs.SingleAsync(log =>
            log.TargetId == word.Id && log.Action == "sensitive_word.created");
        Assert.DoesNotContain(term, createdAudit.MetadataJson.RootElement.GetRawText());
        Assert.Equal(32, createdAudit.IpHash?.Length);

        Assert.Equal(
            SensitiveWordOutcome.DuplicateTerm,
            await CreateService(db).CreateAsync(term.ToUpperInvariant(), "manual", NewAudit(admin.Id)));
        Assert.Equal(
            SensitiveWordOutcome.InvalidInput,
            await CreateService(db).CreateAsync(" ", "manual", NewAudit(admin.Id)));
        Assert.Equal(
            SensitiveWordOutcome.InvalidInput,
            await CreateService(db).CreateAsync($"{term}-x", "pass", NewAudit(admin.Id)));
        Assert.Equal(
            SensitiveWordOutcome.Forbidden,
            await CreateService(db).CreateAsync($"{term}-forbidden", "manual", NewAudit(member.Id)));

        var newTerm = $"{term}-updated";
        Assert.Equal(
            SensitiveWordOutcome.Success,
            await CreateService(db).UpdateAsync(word.Id, newTerm, "manual", NewAudit(admin.Id)));
        Assert.Equal(
            SensitiveWordOutcome.Unchanged,
            await CreateService(db).UpdateAsync(word.Id, newTerm, "manual", NewAudit(admin.Id)));

        Assert.Equal(
            SensitiveWordOutcome.Success,
            await CreateService(db).DisableAsync(word.Id, NewAudit(admin.Id)));
        Assert.Equal(
            SensitiveWordOutcome.Unchanged,
            await CreateService(db).DisableAsync(word.Id, NewAudit(admin.Id)));

        db.ChangeTracker.Clear();
        var updated = await db.SensitiveWords.SingleAsync(candidate => candidate.Id == word.Id);
        Assert.Equal(newTerm, updated.Term);
        Assert.Equal("manual", updated.Action);
        Assert.Equal("disabled", updated.Status);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.TargetId == word.Id && log.Action == "sensitive_word.updated"));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.TargetId == word.Id && log.Action == "sensitive_word.disabled"));
    }

    [DatabaseFact]
    public async Task Policy_defaults_manual_and_updates_with_audit()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var existing = await db.SiteSettings
            .Where(setting => setting.Key == SensitiveWordAdminService.PolicyKey)
            .ToListAsync();
        if (existing.Count > 0)
        {
            db.SiteSettings.RemoveRange(existing);
            await db.SaveChangesAsync();
        }

        var admin = NewUser("policy-admin", UserRoles.Admin);
        var member = NewUser("policy-member", UserRoles.Member);
        db.Users.AddRange(admin, member);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        Assert.Equal("manual", (await service.GetAsync()).PolicyHit);

        Assert.Equal(
            SensitiveWordOutcome.Success,
            await service.UpdatePolicyAsync("reject", NewAudit(admin.Id)));
        Assert.Equal("reject", (await service.GetAsync()).PolicyHit);
        Assert.Equal(
            SensitiveWordOutcome.Unchanged,
            await service.UpdatePolicyAsync("reject", NewAudit(admin.Id)));
        Assert.Equal(
            SensitiveWordOutcome.InvalidInput,
            await service.UpdatePolicyAsync("allow", NewAudit(admin.Id)));
        Assert.Equal(
            SensitiveWordOutcome.Forbidden,
            await service.UpdatePolicyAsync("manual", NewAudit(member.Id)));

        db.ChangeTracker.Clear();
        var setting = await db.SiteSettings.SingleAsync(candidate =>
            candidate.Key == SensitiveWordAdminService.PolicyKey);
        Assert.Equal("reject", setting.Value.RootElement.GetProperty("hit").GetString());
        Assert.Equal(admin.Id, setting.UpdatedBy);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.ActorId == admin.Id && log.Action == "moderation.policy_changed"));
    }

    private static SensitiveWordAdminService CreateService(WhalePodDbContext db)
    {
        return new SensitiveWordAdminService(db, new AdminAccessService(db), TimeProvider.System);
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