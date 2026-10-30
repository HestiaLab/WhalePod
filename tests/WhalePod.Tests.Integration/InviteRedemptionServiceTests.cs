using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class InviteRedemptionServiceTests
{
    private static WhalePodDbContext CreateContext() => ServiceTestSupport.CreateContext();

    [DatabaseFact]
    public async Task Redeem_activates_user_and_consumes_invite()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var admin = NewUser("admin");
        var pending = NewUser("pending");
        var code = $"TEST-{suffix}";
        var invite = NewInvite(admin.Id, code, maxUses: 2);

        db.Users.AddRange(admin, pending);
        db.InviteCodes.Add(invite);
        await db.SaveChangesAsync();

        var outcome = await new InviteRedemptionService(db).RedeemAsync(pending.Id, code);

        Assert.Equal(InviteRedemptionOutcome.Success, outcome);

        db.ChangeTracker.Clear();
        var activated = await db.Users.SingleAsync(user => user.Id == pending.Id);
        var usedInvite = await db.InviteCodes.SingleAsync(candidate => candidate.Id == invite.Id);

        Assert.NotNull(activated.ActivatedAt);
        Assert.Equal(1, usedInvite.UsedCount);
        Assert.Equal(1, await db.InviteRedemptions.CountAsync(redemption => redemption.UserId == pending.Id));
        Assert.Contains(await db.AuditLogs.Where(log => log.ActorId == pending.Id).Select(log => log.Action).ToListAsync(), action => action == "invite.redeem_succeeded");
    }

    [DatabaseFact]
    public async Task Invalid_code_is_audited_and_does_not_activate_user()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var pending = NewUser("invalid");
        db.Users.Add(pending);
        await db.SaveChangesAsync();

        var outcome = await new InviteRedemptionService(db).RedeemAsync(pending.Id, "NOT-A-REAL-CODE");

        Assert.Equal(InviteRedemptionOutcome.Invalid, outcome);

        db.ChangeTracker.Clear();
        var user = await db.Users.SingleAsync(candidate => candidate.Id == pending.Id);
        Assert.Null(user.ActivatedAt);
        Assert.Contains(await db.AuditLogs.Where(log => log.ActorId == pending.Id).Select(log => log.Action).ToListAsync(), action => action == "invite.redeem_failed");
    }

    [DatabaseFact]
    public async Task Already_activated_user_cannot_redeem_again()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var admin = NewUser("admin2");
        var pending = NewUser("twice");
        var code = $"TWICE-{suffix}";

        db.Users.AddRange(admin, pending);
        db.InviteCodes.Add(NewInvite(admin.Id, code, maxUses: 2));
        await db.SaveChangesAsync();

        var service = new InviteRedemptionService(db);
        Assert.Equal(InviteRedemptionOutcome.Success, await service.RedeemAsync(pending.Id, code));

        db.ChangeTracker.Clear();
        Assert.Equal(InviteRedemptionOutcome.AlreadyActivated, await new InviteRedemptionService(db).RedeemAsync(pending.Id, code));
    }

    private static User NewUser(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new User
        {
            Handle = $"{prefix}-{suffix}",
            DisplayName = "测试用户",
            Status = UserStatuses.Active,
            Role = UserRoles.Member
        };
    }

    private static InviteCode NewInvite(Guid createdBy, string code, int maxUses)
    {
        return new InviteCode
        {
            CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code)),
            CreatedBy = createdBy,
            MaxUses = maxUses,
            UsedCount = 0,
            Status = InviteCodeStatuses.Active
        };
    }
}