using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

internal sealed class AgentContentProjector(WhalePodDbContext db)
{
    public async Task<AgentContentSnapshot?> GetCurrentSnapshotAsync(
        ModerationTask task,
        CancellationToken cancellationToken)
    {
        if (task.TargetType == ModerationTargetTypes.Post)
        {
            var post = await db.Posts
                .AsNoTracking()
                .Where(candidate => candidate.Id == task.TargetId
                    && candidate.ModerationStatus == ModerationStatuses.Pending
                    && candidate.DeletedAt == null)
                .Select(candidate => new AgentPostSnapshot(
                    candidate.Id,
                    candidate.Version,
                    candidate.Title,
                    candidate.BodyMd,
                    candidate.CategoryId))
                .SingleOrDefaultAsync(cancellationToken);
            return post is null ? null : new AgentContentSnapshot(post, null, post.Version);
        }

        var comment = await db.Comments
            .AsNoTracking()
            .Where(candidate => candidate.Id == task.TargetId
                && candidate.ModerationStatus == ModerationStatuses.Pending
                && candidate.DeletedAt == null)
            .Select(candidate => new AgentCommentSnapshot(
                candidate.Id,
                candidate.Version,
                candidate.PostId,
                candidate.ParentId,
                candidate.BodyMd))
            .SingleOrDefaultAsync(cancellationToken);
        return comment is null ? null : new AgentContentSnapshot(null, comment, comment.Version);
    }

    public async Task<AgentTaskDetailView?> BuildTaskDetailAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await db.ModerationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var riskHints = ReadRiskHints(task.RiskHintsJson);
        AgentTaskContentView? content;
        if (task.TargetType == ModerationTargetTypes.Post)
        {
            var post = await db.Posts
                .AsNoTracking()
                .Where(candidate => candidate.Id == task.TargetId
                    && candidate.ModerationStatus == ModerationStatuses.Pending
                    && candidate.DeletedAt == null)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.Version,
                    candidate.Title,
                    candidate.BodyMd,
                    candidate.CategoryId
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (post is null)
            {
                return null;
            }

            var category = await db.Categories
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == post.CategoryId, cancellationToken);
            var tagRows = await (
                    from postTag in db.PostTags.AsNoTracking()
                    join tag in db.Tags.AsNoTracking() on postTag.TagId equals tag.Id
                    where postTag.PostId == post.Id && tag.Status == TagStatuses.Active
                    orderby tag.Name
                    select new { tag.Slug, tag.Name })
                .ToListAsync(cancellationToken);
            var artifactRows = await db.Artifacts
                .AsNoTracking()
                .Where(artifact => artifact.PostId == post.Id)
                .OrderBy(artifact => artifact.SortOrder)
                .ThenBy(artifact => artifact.Id)
                .Take(AgentPolicyDefaults.MaxArtifacts)
                .Select(artifact => new
                {
                    artifact.Kind,
                    artifact.Name,
                    artifact.Note,
                    artifact.Content
                })
                .ToListAsync(cancellationToken);
            var imageRows = await db.PostImages
                .AsNoTracking()
                .Where(image => image.PostId == post.Id)
                .OrderBy(image => image.SortOrder)
                .ThenBy(image => image.Id)
                .Take(AgentPolicyDefaults.MaxImages)
                .Select(image => new { image.Url, image.AltText })
                .ToListAsync(cancellationToken);

            var redactions = new HashSet<string>(StringComparer.Ordinal);
            var title = RedactInto(post.Title, 200, redactions);
            var body = RedactInto(post.BodyMd, AgentPolicyDefaults.MaxBodyChars, redactions);
            var artifacts = artifactRows
                .Select(artifact =>
                {
                    var artifactContent = RedactInto(
                        artifact.Content,
                        AgentContentRedactor.MaxArtifactChars,
                        redactions);
                    var note = RedactInto(artifact.Note, 200, redactions);
                    var name = RedactInto(artifact.Name, 200, redactions);
                    return new AgentTaskArtifactView(
                        artifact.Kind,
                        name.Text,
                        note.Text,
                        artifactContent.Text,
                        artifactContent.Redacted || note.Redacted || name.Redacted,
                        artifactContent.Redacted || note.Redacted || name.Redacted
                            ? "content_redacted"
                            : artifactContent.Truncated
                                ? "content_truncated"
                                : null);
                })
                .ToList();
            var images = imageRows
                .Select(image => new AgentTaskImageView(
                    image.Url,
                    RedactInto(image.AltText, 200, redactions).Text))
                .ToList();
            content = new AgentTaskContentView(
                true,
                title.Text,
                body.Text,
                new CategoryView(
                    category.Id,
                    category.Slug,
                    category.Name,
                    category.Description,
                    category.DisplayOrder,
                    category.Status),
                tagRows.Select(tag => new TagSummaryView(tag.Slug, tag.Name)).ToList(),
                artifacts,
                images,
                riskHints,
                redactions.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                AgentPolicyDefaults.UntrustedContentNotice);
        }
        else
        {
            var comment = await db.Comments
                .AsNoTracking()
                .Where(candidate => candidate.Id == task.TargetId
                    && candidate.ModerationStatus == ModerationStatuses.Pending
                    && candidate.DeletedAt == null)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.PostId,
                    candidate.BodyMd
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (comment is null)
            {
                return null;
            }

            var post = await db.Posts
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == comment.PostId, cancellationToken);
            if (post.DeletedAt is not null)
            {
                return null;
            }

            var category = await db.Categories
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == post.CategoryId, cancellationToken);
            var tagRows = await (
                    from postTag in db.PostTags.AsNoTracking()
                    join tag in db.Tags.AsNoTracking() on postTag.TagId equals tag.Id
                    where postTag.PostId == post.Id && tag.Status == TagStatuses.Active
                    orderby tag.Name
                    select new { tag.Slug, tag.Name })
                .ToListAsync(cancellationToken);
            var redactions = new HashSet<string>(StringComparer.Ordinal);
            var body = RedactInto(comment.BodyMd, AgentPolicyDefaults.MaxBodyChars, redactions);
            content = new AgentTaskContentView(
                true,
                "评论",
                body.Text,
                new CategoryView(
                    category.Id,
                    category.Slug,
                    category.Name,
                    category.Description,
                    category.DisplayOrder,
                    category.Status),
                tagRows.Select(tag => new TagSummaryView(tag.Slug, tag.Name)).ToList(),
                [],
                [],
                riskHints,
                redactions.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                AgentPolicyDefaults.UntrustedContentNotice);
        }

        return new AgentTaskDetailView(
            task.Id,
            task.TargetType,
            task.TargetId,
            task.ContentVersion,
            task.ContentHash,
            task.Status,
            task.Priority,
            riskHints,
            task.CreatedAt,
            task.LeaseExpiresAt,
            content,
            AgentPolicyDefaults.Version);
    }

    internal static AgentTaskSummaryView ToSummary(ModerationTask task)
    {
        return new AgentTaskSummaryView(
            task.Id,
            task.TargetType,
            task.TargetId,
            task.ContentVersion,
            task.ContentHash,
            task.Status,
            task.Priority,
            ReadRiskHints(task.RiskHintsJson),
            task.CreatedAt,
            task.LeaseExpiresAt);
    }

    internal static IReadOnlyList<string> ReadRiskHints(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
    }

    private static AgentRedactionResult RedactInto(
        string? value,
        int maxChars,
        HashSet<string> redactions)
    {
        var result = AgentContentRedactor.Redact(value, maxChars);
        foreach (var category in result.Categories)
        {
            redactions.Add(category);
        }

        return result;
    }
}

internal sealed record AgentPostSnapshot(
    Guid Id,
    int Version,
    string Title,
    string BodyMd,
    Guid CategoryId);

internal sealed record AgentCommentSnapshot(
    Guid Id,
    int Version,
    Guid PostId,
    Guid? ParentId,
    string BodyMd);

internal sealed record AgentContentSnapshot(
    AgentPostSnapshot? Post,
    AgentCommentSnapshot? Comment,
    int Version);