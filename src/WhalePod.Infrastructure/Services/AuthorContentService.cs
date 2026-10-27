using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class AuthorContentService(
    WhalePodDbContext db,
    ActorGuard actorGuard)
{
    private static readonly string[] VisibleStatuses =
    [
        ModerationStatuses.Approved,
        ModerationStatuses.Pending,
        ModerationStatuses.Rejected
    ];

    public async Task<FeedPage> GetMyPostsAsync(
        Guid actorId,
        string? status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var normalizedStatus = NormalizeStatus(status);
        var scope = AdminCursor.BuildScope("me-posts", actorId.ToString("N"), normalizedStatus);

        DateTimeOffset? cursorTime = null;
        Guid cursorId = default;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!AdminCursor.TryDecode(cursor, scope, out var decodedTime, out var decodedId))
            {
                return new FeedPage([], null, true);
            }

            cursorTime = decodedTime;
            cursorId = decodedId;
        }

        var query = db.Posts
            .AsNoTracking()
            .Where(post => post.AuthorId == actorId
                && post.DeletedAt == null
                && VisibleStatuses.Contains(post.ModerationStatus));
        if (normalizedStatus is not null)
        {
            query = query.Where(post => post.ModerationStatus == normalizedStatus);
        }

        if (cursorTime is not null)
        {
            query = query.Where(post =>
                post.UpdatedAt < cursorTime
                || (post.UpdatedAt == cursorTime && post.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(post => post.UpdatedAt)
            .ThenByDescending(post => post.Id)
            .Take(limit + 1)
            .Select(post => new
            {
                Post = post,
                Category = db.Categories.Single(category => category.Id == post.CategoryId),
                Author = db.Users.Single(user => user.Id == post.AuthorId),
                LikeCount = db.PostLikes.Count(like => like.PostId == post.Id),
                CommentCount = db.Comments.Count(comment =>
                    comment.PostId == post.Id
                    && comment.ModerationStatus == ModerationStatuses.Approved
                    && comment.DeletedAt == null),
                ArtifactCount = db.Artifacts.Count(artifact => artifact.PostId == post.Id),
                FirstArtifactId = db.Artifacts
                    .Where(artifact => artifact.PostId == post.Id)
                    .OrderBy(artifact => artifact.SortOrder)
                    .ThenBy(artifact => artifact.Id)
                    .Select(artifact => (Guid?)artifact.Id)
                    .FirstOrDefault(),
                ActiveDeviceCount = db.Devices.Count(device =>
                    device.UserId == post.AuthorId && device.Status == DeviceStatuses.Active),
                LastVerifiedAt = db.Devices
                    .Where(device => device.UserId == post.AuthorId && device.Status == DeviceStatuses.Active)
                    .Max(device => (DateTimeOffset?)device.LastVerifiedAt),
                ThumbnailUrl = db.PostImages
                    .Where(image => image.PostId == post.Id)
                    .OrderBy(image => image.SortOrder)
                    .Select(image => image.Url)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limit;
        var pageRows = hasMore ? rows.Take(limit).ToList() : rows;
        var postIds = pageRows.Select(row => row.Post.Id).ToList();
        var tagRows = await (
                from postTag in db.PostTags.AsNoTracking()
                join tag in db.Tags.AsNoTracking() on postTag.TagId equals tag.Id
                where postIds.Contains(postTag.PostId) && tag.Status == TagStatuses.Active
                orderby tag.Name
                select new { postTag.PostId, tag.Slug, tag.Name })
            .ToListAsync(cancellationToken);
        var tagsByPost = tagRows
            .GroupBy(row => row.PostId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TagSummaryView>)group
                    .Select(row => new TagSummaryView(row.Slug, row.Name))
                    .ToList());

        var items = pageRows
            .Select(row => PublicContentService.ToSummary(
                row.Post,
                row.Category,
                row.Author,
                row.LikeCount,
                row.CommentCount,
                row.ArtifactCount,
                row.FirstArtifactId,
                row.ActiveDeviceCount,
                row.LastVerifiedAt,
                row.ThumbnailUrl,
                tagsByPost.GetValueOrDefault(row.Post.Id, [])))
            .ToList();

        var nextCursor = hasMore
            ? AdminCursor.Encode(scope, pageRows[^1].Post.UpdatedAt, pageRows[^1].Post.Id)
            : null;
        return new FeedPage(items, nextCursor, false);
    }

    public async Task<CommentPage> GetMyCommentsAsync(
        Guid actorId,
        string? status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var normalizedStatus = NormalizeStatus(status);
        var scope = AdminCursor.BuildScope("me-comments", actorId.ToString("N"), normalizedStatus);

        DateTimeOffset? cursorTime = null;
        Guid cursorId = default;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!AdminCursor.TryDecode(cursor, scope, out var decodedTime, out var decodedId))
            {
                return new CommentPage([], null, true);
            }

            cursorTime = decodedTime;
            cursorId = decodedId;
        }

        var query = db.Comments
            .AsNoTracking()
            .Where(comment => comment.AuthorId == actorId
                && comment.DeletedAt == null
                && VisibleStatuses.Contains(comment.ModerationStatus));
        if (normalizedStatus is not null)
        {
            query = query.Where(comment => comment.ModerationStatus == normalizedStatus);
        }

        if (cursorTime is not null)
        {
            query = query.Where(comment =>
                comment.UpdatedAt < cursorTime
                || (comment.UpdatedAt == cursorTime && comment.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(comment => comment.UpdatedAt)
            .ThenByDescending(comment => comment.Id)
            .Take(limit + 1)
            .Select(comment => new
            {
                Comment = comment,
                Author = db.Users.Single(user => user.Id == comment.AuthorId),
                ActiveDeviceCount = db.Devices.Count(device =>
                    device.UserId == comment.AuthorId && device.Status == DeviceStatuses.Active),
                LastVerifiedAt = db.Devices
                    .Where(device => device.UserId == comment.AuthorId && device.Status == DeviceStatuses.Active)
                    .Max(device => (DateTimeOffset?)device.LastVerifiedAt)
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limit;
        var pageRows = hasMore ? rows.Take(limit).ToList() : rows;
        var items = pageRows
            .Select(row => new CommentView(
                row.Comment.Id,
                row.Comment.PostId,
                row.Comment.ParentId,
                PublicContentService.ToAuthor(row.Author, row.ActiveDeviceCount, row.LastVerifiedAt),
                row.Comment.BodyMd,
                row.Comment.BodyHtml,
                row.Comment.ModerationStatus,
                row.Comment.IsAccepted,
                row.Comment.LikeCount,
                false,
                row.Comment.Version,
                row.Comment.CreatedAt,
                row.Comment.UpdatedAt))
            .ToList();

        var nextCursor = hasMore
            ? AdminCursor.Encode(scope, pageRows[^1].Comment.UpdatedAt, pageRows[^1].Comment.Id)
            : null;
        return new CommentPage(items, nextCursor, false);
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var normalized = status.Trim().ToLowerInvariant();
        if (normalized is not (ModerationStatuses.Approved
            or ModerationStatuses.Pending
            or ModerationStatuses.Rejected))
        {
            throw ApiProblemException.Validation("status", "status 必须是 approved、pending 或 rejected。");
        }

        return normalized;
    }
}