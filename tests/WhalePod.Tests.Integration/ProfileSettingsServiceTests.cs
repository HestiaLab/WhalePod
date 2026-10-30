using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class ProfileSettingsServiceTests
{
    private static WhalePodDbContext CreateContext() => ServiceTestSupport.CreateContext();

    [DatabaseFact]
    public async Task Update_profile_syncs_public_profile_and_audits_without_values()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var user = NewUser("profile-update");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.UpdateProfileAsync(
            user.Id,
            "  新显示名  ",
            "  新的简介内容  ",
            NewAudit(user.Id));

        Assert.Equal(SettingsUpdateResult.Success, outcome);

        db.ChangeTracker.Clear();
        var updated = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        Assert.Equal("新显示名", updated.DisplayName);
        Assert.Equal("新的简介内容", updated.Bio);

        var publicProfile = Assert.IsType<PublicProfileData>(
            await CreateService(db).GetPublicProfileAsync(user.Handle));
        Assert.Equal("新显示名", publicProfile.DisplayName);
        Assert.Equal("新的简介内容", publicProfile.Bio);

        var audit = await db.AuditLogs.SingleAsync(log =>
            log.ActorId == user.Id && log.Action == "profile.updated");
        Assert.True(audit.MetadataJson.RootElement.GetProperty("displayNameChanged").GetBoolean());
        Assert.True(audit.MetadataJson.RootElement.GetProperty("bioChanged").GetBoolean());
        Assert.DoesNotContain("新显示名", audit.MetadataJson.RootElement.GetRawText());
        Assert.DoesNotContain("新的简介内容", audit.MetadataJson.RootElement.GetRawText());
        Assert.Equal(32, audit.IpHash?.Length);
    }

    [DatabaseFact]
    public async Task Profile_update_rejects_invalid_display_name_and_bio()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var user = NewUser("profile-invalid");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var audit = NewAudit(user.Id);

        Assert.Equal(
            SettingsUpdateResult.InvalidInput,
            (await service.UpdateProfileAsync(user.Id, "   ", null, audit)));
        Assert.Equal(
            SettingsUpdateResult.InvalidInput,
            (await service.UpdateProfileAsync(user.Id, new string('x', 81), null, audit)));
        Assert.Equal(
            SettingsUpdateResult.InvalidInput,
            (await service.UpdateProfileAsync(user.Id, "显示名\n第二行", null, audit)));
        Assert.Equal(
            SettingsUpdateResult.InvalidInput,
            (await service.UpdateProfileAsync(user.Id, "显示名", new string('x', 501), audit)));
        Assert.Equal(
            SettingsUpdateResult.InvalidInput,
            (await service.UpdateProfileAsync(user.Id, "显示名", "简介\u0007", audit)));

        Assert.Equal("测试用户", user.DisplayName);
        Assert.False(await db.AuditLogs.AnyAsync(log =>
            log.ActorId == user.Id && log.Action == "profile.updated"));
    }

    [DatabaseFact]
    public async Task Email_preferences_default_true_and_security_cannot_be_disabled()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var user = NewUser("email-prefs");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var defaults = Assert.IsType<EmailPreferenceData>(await service.GetEmailPreferencesAsync(user.Id));
        Assert.True(defaults.RepliesMentions);
        Assert.True(defaults.ModerationResults);
        Assert.True(defaults.AccountSecurity);

        var outcome = await service.UpdateEmailPreferencesAsync(
            user.Id,
            repliesMentions: false,
            moderationResults: false,
            NewAudit(user.Id));
        Assert.Equal(SettingsUpdateResult.Success, outcome);

        db.ChangeTracker.Clear();
        var rows = await db.EmailPreferences
            .Where(preference => preference.UserId == user.Id)
            .ToDictionaryAsync(preference => preference.Type, preference => preference.Enabled);

        Assert.Equal(3, rows.Count);
        Assert.False(rows[EmailPreferenceTypes.RepliesMentions]);
        Assert.False(rows[EmailPreferenceTypes.ModerationResults]);
        Assert.True(rows[EmailPreferenceTypes.AccountSecurity]);

        var stored = Assert.IsType<EmailPreferenceData>(
            await CreateService(db).GetEmailPreferencesAsync(user.Id));
        Assert.False(stored.RepliesMentions);
        Assert.False(stored.ModerationResults);
        Assert.True(stored.AccountSecurity);

        var audit = await db.AuditLogs.SingleAsync(log =>
            log.ActorId == user.Id && log.Action == "email_pref.updated");
        Assert.False(audit.MetadataJson.RootElement.GetProperty("repliesMentions").GetBoolean());
        Assert.True(audit.MetadataJson.RootElement.GetProperty("accountSecurity").GetBoolean());

        Assert.Equal(
            SettingsUpdateResult.Unchanged,
            (await CreateService(db).UpdateEmailPreferencesAsync(
                user.Id,
                repliesMentions: false,
                moderationResults: false,
                NewAudit(user.Id))));
        Assert.Equal(1, await db.AuditLogs.CountAsync(log =>
            log.ActorId == user.Id && log.Action == "email_pref.updated"));
    }

    [DatabaseFact]
    public async Task Non_active_user_cannot_edit_profile_or_preferences()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var pending = NewUser("profile-pending", activated: false);
        db.Users.Add(pending);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var audit = NewAudit(pending.Id);

        Assert.Null(await service.GetProfileAsync(pending.Id));
        Assert.Equal(
            SettingsUpdateResult.NotFound,
            (await service.UpdateProfileAsync(pending.Id, "新名字", null, audit)));
        Assert.Null(await service.GetEmailPreferencesAsync(pending.Id));
        Assert.Equal(
            SettingsUpdateResult.NotFound,
            (await service.UpdateEmailPreferencesAsync(pending.Id, false, false, audit)));
    }

    [DatabaseFact]
    public async Task Public_profile_hides_pending_banned_and_deleted_users()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var active = NewUser("public-active");
        var muted = NewUser("public-muted", status: UserStatuses.Muted);
        var pending = NewUser("public-pending", activated: false);
        var banned = NewUser("public-banned", status: UserStatuses.Banned);
        var deleted = NewUser("public-deleted", status: UserStatuses.Deleted, deleted: true);

        db.Users.AddRange(active, muted, pending, banned, deleted);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        Assert.NotNull(await service.GetPublicProfileAsync(active.Handle));
        Assert.NotNull(await service.GetPublicProfileAsync(muted.Handle));
        Assert.Null(await service.GetPublicProfileAsync(pending.Handle));
        Assert.Null(await service.GetPublicProfileAsync(banned.Handle));
        Assert.Null(await service.GetPublicProfileAsync(deleted.Handle));
        Assert.Null(await service.GetPublicProfileAsync("no-such-user"));
    }

    [DatabaseFact]
    public async Task Muted_user_can_update_profile_and_email_preferences()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var muted = NewUser("profile-muted", status: UserStatuses.Muted);
        db.Users.Add(muted);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        Assert.Equal(
            SettingsUpdateResult.Success,
            await service.UpdateProfileAsync(muted.Id, "禁言用户", "仍可维护资料", NewAudit(muted.Id)));
        Assert.Equal(
            SettingsUpdateResult.Success,
            await service.UpdateEmailPreferencesAsync(muted.Id, false, false, NewAudit(muted.Id)));
    }

    private static ProfileSettingsService CreateService(WhalePodDbContext db)
    {
        return new ProfileSettingsService(db, TimeProvider.System);
    }

    private static AuditContext NewAudit(Guid actorId)
    {
        return new AuditContext(actorId, "test-request", new byte[32]);
    }

    private static User NewUser(
        string prefix,
        string status = UserStatuses.Active,
        bool activated = true,
        bool deleted = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var user = new User
        {
            Handle = $"{prefix}-{suffix}",
            DisplayName = "测试用户",
            Status = status,
            Role = UserRoles.Member,
            ActivatedAt = activated ? DateTimeOffset.UtcNow : null
        };

        if (status == UserStatuses.Muted)
        {
            user.StatusReason = "测试禁言";
            user.StatusExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
        }
        else if (status == UserStatuses.Banned)
        {
            user.StatusReason = "测试封禁";
        }
        else if (status == UserStatuses.Deleted)
        {
            user.DeletedAt = DateTimeOffset.UtcNow;
        }

        if (deleted && user.DeletedAt is null)
        {
            user.DeletedAt = DateTimeOffset.UtcNow;
        }

        return user;
    }
}