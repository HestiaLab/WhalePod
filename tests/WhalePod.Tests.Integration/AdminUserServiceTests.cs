using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class AdminUserServiceTests
{
    [DatabaseFact]
    public async Task List_and_detail_filter_users_without_exposing_private_fields()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var admin = ServiceTestSupport.NewUser("user-admin", UserRoles.Admin);
        var member = ServiceTestSupport.NewUser("user-search", trustLevel: 2);
        member.Email = "private@example.com";
        member.GitHubId = Random.Shared.NextInt64(1, long.MaxValue);
        var muted = ServiceTestSupport.NewUser(
            "user-muted",
            status: UserStatuses.Muted,
            statusReason: "测试禁言",
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(2),
            trustLevel: 1);
        db.Users.AddRange(admin, member, muted);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var page = await services.AdminUsers.ListAsync(
            admin.Id,
            "user-search",
            UserStatuses.Active,
            2,
            50,
            null);
        Assert.False(page.InvalidCursor);
        var item = Assert.Single(page.Items, user => user.Id == member.Id);
        Assert.Equal(member.Handle, item.Handle);
        Assert.Equal(2, item.TrustLevel);
        Assert.DoesNotContain("private@example.com", JsonSerializer.Serialize(item));
        Assert.DoesNotContain(member.GitHubId.Value.ToString(), JsonSerializer.Serialize(item));

        var mutedPage = await services.AdminUsers.ListAsync(
            admin.Id,
            null,
            UserStatuses.Muted,
            1,
            50,
            null);
        Assert.Contains(mutedPage.Items, user => user.Id == muted.Id && user.StatusReason == "测试禁言");

        var detail = await services.AdminUsers.GetAsync(admin.Id, member.Id);
        Assert.Equal(member.Id, detail.Id);
        Assert.Equal(UserRoles.Member, detail.Role);
        Assert.Null(detail.StatusReason);
        await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AdminUsers.GetAsync(admin.Id, Guid.NewGuid()));
    }

    [DatabaseFact]
    public async Task Status_and_trust_changes_validate_transitions_write_audit_and_notifications()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var admin = ServiceTestSupport.NewUser("user-actions-admin", UserRoles.Admin);
        var target = ServiceTestSupport.NewUser("user-actions-target");
        db.Users.AddRange(admin, target);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var noExpiry = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AdminUsers.ChangeStatusAsync(
                admin.Id,
                target.Id,
                UserStatuses.Muted,
                "测试禁言",
                null,
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, noExpiry.Code);

        var expiry = DateTimeOffset.UtcNow.AddDays(7);
        var muted = await services.AdminUsers.ChangeStatusAsync(
            admin.Id,
            target.Id,
            UserStatuses.Muted,
            "多次发布推广",
            expiry,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(UserStatuses.Muted, muted.Status);
        Assert.Equal("多次发布推广", muted.StatusReason);
        Assert.NotNull(muted.StatusExpiresAt);

        var muteAgain = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AdminUsers.ChangeStatusAsync(
                admin.Id,
                target.Id,
                UserStatuses.Muted,
                "重复禁言",
                expiry,
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Conflict, muteAgain.Code);

        var banned = await services.AdminUsers.ChangeStatusAsync(
            admin.Id,
            target.Id,
            UserStatuses.Banned,
            "升级为封禁",
            null,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(UserStatuses.Banned, banned.Status);
        Assert.Null(banned.StatusExpiresAt);

        var active = await services.AdminUsers.ChangeStatusAsync(
            admin.Id,
            target.Id,
            UserStatuses.Active,
            "申诉通过，解除处罚",
            null,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(UserStatuses.Active, active.Status);
        Assert.Null(active.StatusReason);
        Assert.Null(active.StatusExpiresAt);

        var promoted = await services.AdminUsers.ChangeTrustLevelAsync(
            admin.Id,
            target.Id,
            3,
            "持续贡献可复现内容",
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(3, promoted.TrustLevel);
        Assert.Equal(3, (await services.AdminUsers.GetAsync(admin.Id, target.Id)).TrustLevel);

        var selfStatus = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AdminUsers.ChangeStatusAsync(
                admin.Id,
                admin.Id,
                UserStatuses.Banned,
                "不能处罚自己",
                null,
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, selfStatus.Code);

        await services.AdminUsers.ChangeTrustLevelAsync(
            admin.Id,
            admin.Id,
            4,
            "自提升可信等级",
            ServiceTestSupport.NewAudit(admin.Id));
        var selfDemote = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AdminUsers.ChangeTrustLevelAsync(
                admin.Id,
                admin.Id,
                1,
                "自降级",
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, selfDemote.Code);

        Assert.True(await db.AuditLogs.CountAsync(log =>
            log.TargetId == target.Id
            && log.Action.StartsWith("user.")
            && log.IpHash != null) >= 4);
        Assert.True(await db.Notifications.CountAsync(notification =>
            notification.UserId == target.Id
            && notification.Type == NotificationTypes.AccountStatusChanged) >= 4);
    }

    [DatabaseFact]
    public async Task Admin_cannot_mutate_deleted_users_or_ban_last_admin_self()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var admin = ServiceTestSupport.NewUser("user-last-admin", UserRoles.Admin);
        var deleted = ServiceTestSupport.NewUser("user-deleted", status: UserStatuses.Deleted);
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        db.Users.AddRange(admin, deleted);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var selfBan = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AdminUsers.ChangeStatusAsync(
                admin.Id,
                admin.Id,
                UserStatuses.Muted,
                "不能处罚自己",
                DateTimeOffset.UtcNow.AddDays(1),
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Forbidden, selfBan.Code);

        var deletedTarget = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AdminUsers.ChangeStatusAsync(
                admin.Id,
                deleted.Id,
                UserStatuses.Active,
                "恢复已注销账号",
                null,
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.Conflict, deletedTarget.Code);
    }
}