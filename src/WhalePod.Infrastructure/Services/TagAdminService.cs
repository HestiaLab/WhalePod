using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum TagMutationResult
{
    Success,
    Unchanged,
    NotFound,
    Forbidden,
    DuplicateName,
    InvalidName
}

public sealed record TagAdminItem(
    Guid Id,
    string Slug,
    string Name,
    string Status,
    int PublicPostCount,
    DateTimeOffset UpdatedAt);

public sealed class TagAdminService(
    WhalePodDbContext db,
    AdminAccessService adminAccess,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<TagAdminItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tags = await db.Tags
            .AsNoTracking()
            .OrderBy(tag => tag.Status)
            .ThenBy(tag => tag.Name)
            .ToListAsync(cancellationToken);

        var counts = await (
                from postTag in db.PostTags
                join post in db.Posts on postTag.PostId equals post.Id
                where post.ModerationStatus == ModerationStatuses.Approved && post.DeletedAt == null
                group postTag by postTag.TagId
                into grouped
                select new { TagId = grouped.Key, Count = grouped.Count() })
            .ToDictionaryAsync(item => item.TagId, item => item.Count, cancellationToken);

        return tags
            .Select(tag => new TagAdminItem(
                tag.Id,
                tag.Slug,
                tag.Name,
                tag.Status,
                counts.GetValueOrDefault(tag.Id),
                tag.UpdatedAt))
            .ToList();
    }

    public async Task<TagMutationResult> RenameAsync(
        Guid tagId,
        string newName,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return TagMutationResult.Forbidden;
        }

        var tag = await db.Tags.SingleOrDefaultAsync(candidate => candidate.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return TagMutationResult.NotFound;
        }

        var normalized = (newName ?? string.Empty).Trim();
        var runeCount = normalized.EnumerateRunes().Count();
        if (runeCount is < 1 or > 30 || normalized.Any(char.IsControl))
        {
            return TagMutationResult.InvalidName;
        }

        if (string.Equals(tag.Name, normalized, StringComparison.Ordinal))
        {
            return TagMutationResult.Unchanged;
        }

        var duplicate = await db.Tags
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id != tagId && candidate.Name == normalized, cancellationToken);
        if (duplicate)
        {
            return TagMutationResult.DuplicateName;
        }

        var previousName = tag.Name;
        var now = timeProvider.GetUtcNow();
        tag.Name = normalized;
        tag.UpdatedAt = now;

        db.AuditLogs.Add(NewAudit(
            audit,
            "tag.renamed",
            tag.Id,
            new { oldName = previousName, newName = normalized },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
        {
            SqlState: "23505",
            ConstraintName: "uq_tag_name"
        })
        {
            db.ChangeTracker.Clear();
            return TagMutationResult.DuplicateName;
        }

        return TagMutationResult.Success;
    }

    public async Task<TagMutationResult> SetVisibilityAsync(
        Guid tagId,
        bool hidden,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return TagMutationResult.Forbidden;
        }

        var tag = await db.Tags.SingleOrDefaultAsync(candidate => candidate.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return TagMutationResult.NotFound;
        }

        var newStatus = hidden ? TagStatuses.Hidden : TagStatuses.Active;
        if (tag.Status == newStatus)
        {
            return TagMutationResult.Unchanged;
        }

        var publicPostCount = await (
                from postTag in db.PostTags
                join post in db.Posts on postTag.PostId equals post.Id
                where postTag.TagId == tagId
                    && post.ModerationStatus == ModerationStatuses.Approved
                    && post.DeletedAt == null
                select postTag)
            .CountAsync(cancellationToken);

        var previousStatus = tag.Status;
        var now = timeProvider.GetUtcNow();
        tag.Status = newStatus;
        tag.UpdatedAt = now;

        db.AuditLogs.Add(NewAudit(
            audit,
            hidden ? "tag.hidden" : "tag.restored",
            tag.Id,
            new { oldStatus = previousStatus, newStatus, publicPostCount },
            now));

        await db.SaveChangesAsync(cancellationToken);
        return TagMutationResult.Success;
    }

    private static AuditLog NewAudit(
        AuditContext audit,
        string action,
        Guid tagId,
        object metadata,
        DateTimeOffset createdAt)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = audit.ActorId,
            Action = action,
            TargetType = "tag",
            TargetId = tagId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(metadata),
            CreatedAt = createdAt
        };
    }
}