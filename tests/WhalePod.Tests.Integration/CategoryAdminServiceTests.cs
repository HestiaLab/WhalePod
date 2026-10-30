using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class CategoryAdminServiceTests
{
    [DatabaseFact]
    public async Task Category_lifecycle_is_admin_only_validated_and_audited()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("category-admin", UserRoles.Admin);
        var member = ServiceTestSupport.NewUser("category-member");
        var author = ServiceTestSupport.NewUser("category-author");
        db.Users.AddRange(admin, member, author);
        await db.SaveChangesAsync();

        var service = new CategoryAdminService(
            db,
            new AdminAccessService(db),
            TimeProvider.System);
        var slug = $"cat-admin-{suffix}";
        var created = await service.CreateAsync(
            admin.Id,
            slug,
            "管理后台板块",
            "用于分类管理测试",
            10,
            requiresKernel: false,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(CategoryMutationOutcome.Success, created.Outcome);
        Assert.NotNull(created.CategoryId);

        var duplicate = await service.CreateAsync(
            admin.Id,
            slug,
            "重复板块",
            null,
            20,
            requiresKernel: false,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(CategoryMutationOutcome.DuplicateSlug, duplicate.Outcome);

        var invalid = await service.CreateAsync(
            admin.Id,
            "Invalid Slug",
            string.Empty,
            null,
            20,
            requiresKernel: false,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(CategoryMutationOutcome.InvalidInput, invalid.Outcome);

        var updated = await service.UpdateAsync(
            admin.Id,
            created.CategoryId!.Value,
            "管理后台板块（更新）",
            "更新说明",
            5,
            requiresKernel: false,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(CategoryMutationOutcome.Success, updated.Outcome);

        var category = await db.Categories.SingleAsync(item => item.Id == created.CategoryId);
        db.Posts.Add(ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            ModerationStatuses.Approved,
            title: $"分类停用影响的公开帖子 {suffix}"));
        await db.SaveChangesAsync();

        var unconfirmed = await service.SetStatusAsync(
            admin.Id,
            category.Id,
            disabled: true,
            confirmImpact: false,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(CategoryMutationOutcome.ConfirmationRequired, unconfirmed.Outcome);

        var disabled = await service.SetStatusAsync(
            admin.Id,
            category.Id,
            disabled: true,
            confirmImpact: true,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(CategoryMutationOutcome.Success, disabled.Outcome);
        db.ChangeTracker.Clear();
        Assert.Equal(
            CategoryStatuses.Disabled,
            (await db.Categories.SingleAsync(item => item.Id == category.Id)).Status);
        var actions = await db.AuditLogs
            .Where(log => log.TargetId == category.Id)
            .Select(log => log.Action)
            .ToListAsync();
        Assert.Contains("category.created", actions);
        Assert.Contains("category.updated", actions);
        Assert.Contains("category.disabled", actions);

        var forbidden = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.ListAsync(member.Id));
        Assert.Equal(ApiErrorCodes.Forbidden, forbidden.Code);

        var missing = await service.UpdateAsync(
            admin.Id,
            Guid.NewGuid(),
            "不存在",
            null,
            1,
            requiresKernel: false,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(CategoryMutationOutcome.NotFound, missing.Outcome);
    }
}