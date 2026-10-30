using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class InviteAdminServiceTests
{
    private static WhalePodDbContext CreateContext() => ServiceTestSupport.CreateContext();

    [DatabaseFact]
    public async Task Create_stores_hash_and_returns_plaintext_once_with_audit()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("invite-admin", UserRoles.Admin);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToOffset(TimeSpan.FromHours(8));
        var service = CreateService(db);
        var result = await service.CreateAsync(
            "  10 月测试批次  ",
            5,
            expiresAt,
            NewAudit(admin.Id));

        Assert.Equal(InviteAdminOutcome.Success, result.Outcome);
        var created = Assert.IsType<InviteCreationData>(result.Invite);
        Assert.StartsWith("WP-", created.PlaintextCode);
        Assert.Equal($"…{created.PlaintextCode[^4..]}", created.CodeHint);

        db.ChangeTracker.Clear();
        var invite = await db.InviteCodes.SingleAsync(candidate => candidate.Id == created.Id);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(created.PlaintextCode)), invite.CodeHash);
        Assert.Equal(created.CodeHint, invite.CodeHint);
        Assert.Equal(5, invite.MaxUses);
        Assert.Equal(0, invite.UsedCount);
        Assert.Equal(InviteCodeStatuses.Active, invite.Status);
        Assert.Equal("10 月测试批次", invite.Note);
        Assert.Equal(admin.Id, invite.CreatedBy);
        Assert.Equal(TimeSpan.Zero, invite.ExpiresAt!.Value.Offset);
        Assert.True(Math.Abs((invite.ExpiresAt.Value - expiresAt.ToUniversalTime()).Ticks) < 10);
        Assert.Equal(TimeSpan.Zero, created.ExpiresAt!.Value.Offset);
        Assert.True(Math.Abs((created.ExpiresAt.Value - expiresAt.ToUniversalTime()).Ticks) < 10);

        var audit = await db.AuditLogs.SingleAsync(log =>
            log.TargetId == invite.Id && log.Action == "invite.created");
        Assert.Equal(admin.Id, audit.ActorId);
        Assert.Equal(32, audit.IpHash?.Length);
        Assert.DoesNotContain(created.PlaintextCode, audit.MetadataJson.RootElement.GetRawText());
    }

    [DatabaseFact]
    public async Task Create_rejects_invalid_limits_note_and_expiry()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("invite-invalid", UserRoles.Admin);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var audit = NewAudit(admin.Id);

        Assert.Equal(
            InviteAdminOutcome.InvalidInput,
            (await service.CreateAsync(null, 0, null, audit)).Outcome);
        Assert.Equal(
            InviteAdminOutcome.InvalidInput,
            (await service.CreateAsync(null, 1, DateTimeOffset.UtcNow.AddMinutes(-1), audit)).Outcome);
        Assert.Equal(
            InviteAdminOutcome.InvalidInput,
            (await service.CreateAsync(new string('x', 201), 1, null, audit)).Outcome);

        Assert.Equal(0, await db.InviteCodes.CountAsync(invite => invite.CreatedBy == admin.Id));
    }

    [DatabaseFact]
    public async Task Batch_disable_only_changes_unused_active_invites_and_audits()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("invite-disable", UserRoles.Admin);
        var unused = NewInvite(admin.Id, InviteCodeStatuses.Active, usedCount: 0, maxUses: 2);
        var used = NewInvite(admin.Id, InviteCodeStatuses.Active, usedCount: 1, maxUses: 2);
        var exhausted = NewInvite(admin.Id, InviteCodeStatuses.Active, usedCount: 2, maxUses: 2);
        var disabled = NewInvite(admin.Id, InviteCodeStatuses.Disabled, usedCount: 0, maxUses: 1);

        db.Users.Add(admin);
        db.InviteCodes.AddRange(unused, used, exhausted, disabled);
        await db.SaveChangesAsync();

        var missingId = Guid.NewGuid();
        var result = await CreateService(db).DisableUnusedAsync(
            [unused.Id, used.Id, exhausted.Id, disabled.Id, missingId],
            NewAudit(admin.Id));

        Assert.Equal(InviteAdminOutcome.Success, result.Outcome);
        Assert.Equal(5, result.Requested);
        Assert.Equal(1, result.Disabled);
        Assert.Equal(4, result.Skipped);

        db.ChangeTracker.Clear();
        var reloaded = await db.InviteCodes
            .Where(invite => invite.CreatedBy == admin.Id)
            .ToDictionaryAsync(invite => invite.Id);

        Assert.Equal(InviteCodeStatuses.Disabled, reloaded[unused.Id].Status);
        Assert.NotNull(reloaded[unused.Id].DisabledAt);
        Assert.Equal(InviteCodeStatuses.Active, reloaded[used.Id].Status);
        Assert.Equal(InviteCodeStatuses.Active, reloaded[exhausted.Id].Status);
        Assert.Equal(InviteCodeStatuses.Disabled, reloaded[disabled.Id].Status);
        Assert.NotNull(reloaded[disabled.Id].DisabledAt);

        var audit = await db.AuditLogs.SingleAsync(log =>
            log.ActorId == admin.Id && log.Action == "invite.bulk_disabled");
        Assert.Equal(1, audit.MetadataJson.RootElement.GetProperty("disabled").GetInt32());
        Assert.Equal(4, audit.MetadataJson.RootElement.GetProperty("skipped").GetInt32());
        Assert.Equal(
            unused.Id,
            audit.MetadataJson.RootElement.GetProperty("inviteIds").EnumerateArray().Single().GetGuid());
    }

    [DatabaseFact]
    public async Task Non_admin_cannot_disable_invites()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var member = NewUser("invite-member", UserRoles.Member);
        var invite = NewInvite(member.Id, InviteCodeStatuses.Active, usedCount: 0, maxUses: 1);
        db.Users.Add(member);
        db.InviteCodes.Add(invite);
        await db.SaveChangesAsync();

        var result = await CreateService(db).DisableUnusedAsync([invite.Id], NewAudit(member.Id));

        Assert.Equal(InviteAdminOutcome.Forbidden, result.Outcome);

        db.ChangeTracker.Clear();
        Assert.Equal(
            InviteCodeStatuses.Active,
            (await db.InviteCodes.SingleAsync(candidate => candidate.Id == invite.Id)).Status);
    }

    [DatabaseFact]
    public async Task List_filters_by_hint_state_member_and_code_lookup_works()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var admin = NewUser("invite-list", UserRoles.Admin);
        var invitee = NewUser("invitee", UserRoles.Member);
        var plaintext = $"WP-TEST-{suffix}";
        var hint = $"…{plaintext[^4..]}";
        var active = NewInvite(admin.Id, InviteCodeStatuses.Active, usedCount: 1, maxUses: 5, hint: hint);
        active.CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

        var expired = NewInvite(
            admin.Id,
            InviteCodeStatuses.Active,
            usedCount: 0,
            maxUses: 2,
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1),
            hint: $"…{suffix[..4].ToUpperInvariant()}");
        var exhausted = NewInvite(
            admin.Id,
            InviteCodeStatuses.Active,
            usedCount: 2,
            maxUses: 2,
            hint: $"…{suffix[4..].ToUpperInvariant()}");

        db.Users.AddRange(admin, invitee);
        db.InviteCodes.AddRange(active, expired, exhausted);
        db.InviteRedemptions.Add(new InviteRedemption
        {
            InviteCodeId = active.Id,
            UserId = invitee.Id,
            InvitedBy = admin.Id
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var byHint = await service.ListAsync(admin.Id, "active", hint, null, 1);
        var activeItem = Assert.Single(byHint.Items);
        Assert.Equal(active.Id, activeItem.Id);
        Assert.Equal("active", activeItem.State);
        Assert.Equal(new[] { invitee.Handle }, activeItem.RecentInvitees);

        var byInvitee = await service.ListAsync(admin.Id, null, null, invitee.Handle, 1);
        Assert.Contains(byInvitee.Items, item => item.Id == active.Id);

        var byCreator = await service.ListAsync(admin.Id, null, null, admin.Handle, 1);
        Assert.Contains(byCreator.Items, item => item.Id == active.Id);

        Assert.Single((await service.ListAsync(admin.Id, "expired", expired.CodeHint![^4..], null, 1)).Items);
        Assert.Single((await service.ListAsync(admin.Id, "exhausted", exhausted.CodeHint![^4..], null, 1)).Items);

        Assert.Equal(active.Id, await service.FindInviteIdByCodeAsync(plaintext));
        Assert.Null(await service.FindInviteIdByCodeAsync($"{plaintext}-wrong"));
    }

    [DatabaseFact]
    public async Task Detail_paginates_redemptions_and_shows_inviter()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var admin = NewUser("invite-detail", UserRoles.Admin);
        var invite = NewInvite(admin.Id, InviteCodeStatuses.Active, usedCount: 21, maxUses: 25);
        db.Users.Add(admin);
        db.InviteCodes.Add(invite);
        await db.SaveChangesAsync();

        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        for (var index = 0; index < 21; index++)
        {
            var invitee = new User
            {
                Handle = $"detail-{suffix}-{index:D2}",
                DisplayName = "被邀请用户",
                Status = UserStatuses.Active,
                Role = UserRoles.Member,
                ActivatedAt = baseTime
            };
            db.Users.Add(invitee);
            db.InviteRedemptions.Add(new InviteRedemption
            {
                InviteCodeId = invite.Id,
                UserId = invitee.Id,
                InvitedBy = admin.Id,
                CreatedAt = baseTime.AddMinutes(index)
            });
        }

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var firstPage = Assert.IsType<InviteAdminDetail>(await service.GetDetailAsync(invite.Id, 1));
        Assert.Equal(21, firstPage.TotalRedemptions);
        Assert.Equal(20, firstPage.Redemptions.Count);
        Assert.All(firstPage.Redemptions, item => Assert.Equal(admin.Handle, item.InvitedByHandle));
        Assert.Equal($"detail-{suffix}-20", firstPage.Redemptions[0].UserHandle);

        var secondPage = Assert.IsType<InviteAdminDetail>(await service.GetDetailAsync(invite.Id, 2));
        var last = Assert.Single(secondPage.Redemptions);
        Assert.Equal($"detail-{suffix}-00", last.UserHandle);

        Assert.Null(await service.GetDetailAsync(Guid.NewGuid(), 1));
    }

    private static InviteAdminService CreateService(WhalePodDbContext db)
    {
        return new InviteAdminService(db, new AdminAccessService(db), TimeProvider.System);
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

    private static InviteCode NewInvite(
        Guid createdBy,
        string status,
        int usedCount,
        int maxUses,
        DateTimeOffset? expiresAt = null,
        string? hint = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new InviteCode
        {
            CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes($"TEST-{suffix}")),
            CodeHint = hint ?? $"…{suffix[..4].ToUpperInvariant()}",
            CreatedBy = createdBy,
            MaxUses = maxUses,
            UsedCount = usedCount,
            ExpiresAt = expiresAt,
            Status = status,
            Note = "测试邀请码",
            DisabledAt = status == InviteCodeStatuses.Disabled ? DateTimeOffset.UtcNow : null
        };
    }
}