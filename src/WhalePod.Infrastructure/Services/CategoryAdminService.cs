using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum CategoryMutationOutcome
{
    Success,
    Unchanged,
    InvalidInput,
    DuplicateSlug,
    NotFound,
    ConfirmationRequired
}

public sealed record CategoryMutationResult(
    CategoryMutationOutcome Outcome,
    Guid? CategoryId = null,
    string? Field = null);

public sealed record CategoryAdminItem(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    int DisplayOrder,
    string Status,
    int PublicPostCount,
    DateTimeOffset UpdatedAt,
    bool RequiresKernel);

public sealed class CategoryAdminService(
    WhalePodDbContext db,
    AdminAccessService adminAccess,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<CategoryAdminItem>> ListAsync(
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(actorId, cancellationToken);
        var rows = await db.Categories
            .AsNoTracking()
            .OrderBy(category => category.DisplayOrder)
            .ThenBy(category => category.Name)
            .Select(category => new
            {
                Category = category,
                PublicPostCount = db.Posts.Count(post =>
                    post.CategoryId == category.Id
                    && post.ModerationStatus == ModerationStatuses.Approved
                    && post.DeletedAt == null)
            })
            .ToListAsync(cancellationToken);
        return rows
            .Select(row => new CategoryAdminItem(
                row.Category.Id,
                row.Category.Slug,
                row.Category.Name,
                row.Category.Description,
                row.Category.DisplayOrder,
                row.Category.Status,
                row.PublicPostCount,
                row.Category.UpdatedAt,
                row.Category.RequiresKernel))
            .ToList();
    }

    public async Task<CategoryAdminItem?> GetAsync(
        Guid actorId,
        Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(actorId, cancellationToken);
        var row = await db.Categories
            .AsNoTracking()
            .Where(category => category.Id == categoryId)
            .Select(category => new
            {
                Category = category,
                PublicPostCount = db.Posts.Count(post =>
                    post.CategoryId == category.Id
                    && post.ModerationStatus == ModerationStatuses.Approved
                    && post.DeletedAt == null)
            })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new CategoryAdminItem(
                row.Category.Id,
                row.Category.Slug,
                row.Category.Name,
                row.Category.Description,
                row.Category.DisplayOrder,
                row.Category.Status,
                row.PublicPostCount,
                row.Category.UpdatedAt,
                row.Category.RequiresKernel);
    }

    public async Task<CategoryMutationResult> CreateAsync(
        Guid actorId,
        string? slug,
        string? name,
        string? description,
        int displayOrder,
        bool requiresKernel,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(actorId, cancellationToken);
        if (!TryNormalize(slug, name, description, displayOrder, out var normalized, out var field))
        {
            return new CategoryMutationResult(CategoryMutationOutcome.InvalidInput, Field: field);
        }

        if (await db.Categories.AnyAsync(
                category => category.Slug == normalized.Slug,
                cancellationToken))
        {
            return new CategoryMutationResult(CategoryMutationOutcome.DuplicateSlug, Field: "slug");
        }

        var now = timeProvider.GetUtcNow();
        var entity = new Category
        {
            Slug = normalized.Slug,
            Name = normalized.Name,
            Description = normalized.Description,
            DisplayOrder = normalized.DisplayOrder,
            Status = CategoryStatuses.Active,
            RequiresKernel = requiresKernel,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Categories.Add(entity);
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "category.created",
            "category",
            entity.Id,
            new
            {
                slug = entity.Slug,
                name = entity.Name,
                displayOrder = entity.DisplayOrder
            },
            now));
        await db.SaveChangesAsync(cancellationToken);
        return new CategoryMutationResult(CategoryMutationOutcome.Success, entity.Id);
    }

    public async Task<CategoryMutationResult> UpdateAsync(
        Guid actorId,
        Guid categoryId,
        string? name,
        string? description,
        int displayOrder,
        bool requiresKernel,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(actorId, cancellationToken);
        var category = await db.Categories
            .SingleOrDefaultAsync(candidate => candidate.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return new CategoryMutationResult(CategoryMutationOutcome.NotFound);
        }

        if (!TryNormalize(category.Slug, name, description, displayOrder, out var normalized, out var field))
        {
            return new CategoryMutationResult(CategoryMutationOutcome.InvalidInput, categoryId, field);
        }

        if (category.Name == normalized.Name
            && category.Description == normalized.Description
            && category.DisplayOrder == normalized.DisplayOrder
            && category.RequiresKernel == requiresKernel)
        {
            return new CategoryMutationResult(CategoryMutationOutcome.Unchanged, categoryId);
        }

        var previousName = category.Name;
        var previousDescription = category.Description;
        var previousOrder = category.DisplayOrder;
        var previousRequiresKernel = category.RequiresKernel;
        category.Name = normalized.Name;
        category.Description = normalized.Description;
        category.DisplayOrder = normalized.DisplayOrder;
        category.RequiresKernel = requiresKernel;
        category.UpdatedAt = timeProvider.GetUtcNow();
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "category.updated",
            "category",
            category.Id,
            new
            {
                previousName,
                name = category.Name,
                previousDescription,
                description = category.Description,
                previousDisplayOrder = previousOrder,
                displayOrder = category.DisplayOrder,
                previousRequiresKernel,
                requiresKernel = category.RequiresKernel
            },
            category.UpdatedAt));
        await db.SaveChangesAsync(cancellationToken);
        return new CategoryMutationResult(CategoryMutationOutcome.Success, category.Id);
    }

    public async Task<CategoryMutationResult> SetStatusAsync(
        Guid actorId,
        Guid categoryId,
        bool disabled,
        bool confirmImpact,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(actorId, cancellationToken);
        var category = await db.Categories
            .SingleOrDefaultAsync(candidate => candidate.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return new CategoryMutationResult(CategoryMutationOutcome.NotFound);
        }

        var targetStatus = disabled ? CategoryStatuses.Disabled : CategoryStatuses.Active;
        if (category.Status == targetStatus)
        {
            return new CategoryMutationResult(CategoryMutationOutcome.Unchanged, categoryId);
        }

        var publicPostCount = await db.Posts.CountAsync(
            post => post.CategoryId == categoryId
                && post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null,
            cancellationToken);
        if (disabled && publicPostCount > 0 && !confirmImpact)
        {
            return new CategoryMutationResult(CategoryMutationOutcome.ConfirmationRequired, categoryId);
        }

        category.Status = targetStatus;
        category.UpdatedAt = timeProvider.GetUtcNow();
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            disabled ? "category.disabled" : "category.enabled",
            "category",
            category.Id,
            new
            {
                publicPostCount,
                slug = category.Slug
            },
            category.UpdatedAt));
        await db.SaveChangesAsync(cancellationToken);
        return new CategoryMutationResult(CategoryMutationOutcome.Success, category.Id);
    }

    private async Task RequireAdminAsync(Guid actorId, CancellationToken cancellationToken)
    {
        if (!await adminAccess.IsAdminAsync(actorId, cancellationToken))
        {
            throw ApiProblemException.Forbidden();
        }
    }

    private static bool TryNormalize(
        string? slug,
        string? name,
        string? description,
        int displayOrder,
        out NormalizedCategory normalized,
        out string? field)
    {
        normalized = default;
        var normalizedSlug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedName = (name ?? string.Empty).Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (!Regex.IsMatch(normalizedSlug, "^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant))
        {
            field = "slug";
            return false;
        }

        if (RuneHelper.Count(normalizedName) is < 1 or > 40 || normalizedName.Contains('\0'))
        {
            field = "name";
            return false;
        }

        if (normalizedDescription is not null
            && (RuneHelper.Count(normalizedDescription) > 200 || normalizedDescription.Contains('\0')))
        {
            field = "description";
            return false;
        }

        if (displayOrder is < 0 or > 1000)
        {
            field = "displayOrder";
            return false;
        }

        field = null;
        normalized = new NormalizedCategory(
            normalizedSlug,
            normalizedName,
            normalizedDescription,
            displayOrder);
        return true;
    }

    private readonly record struct NormalizedCategory(
        string Slug,
        string Name,
        string? Description,
        int DisplayOrder);
}