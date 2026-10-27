using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class PublicSearchService(WhalePodDbContext db)
{
    public async Task<SearchResultView> SearchAsync(
        string? query,
        string? type,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? string.Empty).Trim();
        var normalizedType = string.IsNullOrWhiteSpace(type) ? "posts" : type.Trim().ToLowerInvariant();
        var emptyCounts = new SearchCountsView(0, 0, 0);
        if (normalizedQuery.Length == 0)
        {
            return new SearchResultView(
                normalizedQuery,
                normalizedType,
                emptyCounts,
                [],
                [],
                [],
                null,
                InvalidQuery: true,
                InvalidCursor: false);
        }

        var escaped = EscapeLike(normalizedQuery);
        var pattern = $"%{escaped}%";
        var prefix = $"{escaped}%";
        var queryHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedQuery)))[..16];

        var postsQuery = db.Posts
            .AsNoTracking()
            .Where(post => post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null
                && (EF.Functions.ILike(post.Title, pattern)
                    || EF.Functions.ILike(post.BodyMd, pattern)
                    || EF.Functions.ILike(post.Excerpt, pattern)
                    || db.Users.Any(user => user.Id == post.AuthorId
                        && (EF.Functions.ILike(user.Handle, pattern)
                            || EF.Functions.ILike(user.DisplayName, pattern)
                            || (user.Bio != null && EF.Functions.ILike(user.Bio, pattern))))
                    || db.PostTags.Any(postTag => postTag.PostId == post.Id
                        && db.Tags.Any(tag => tag.Id == postTag.TagId
                            && tag.Status == TagStatuses.Active
                            && (EF.Functions.ILike(tag.Name, pattern)
                                || EF.Functions.ILike(tag.Slug, pattern))))));

        var usersQuery = db.Users
            .AsNoTracking()
            .Where(user => (user.Status == UserStatuses.Active || user.Status == UserStatuses.Muted)
                && user.ActivatedAt != null
                && user.DeletedAt == null
                && (EF.Functions.ILike(user.Handle, pattern)
                    || EF.Functions.ILike(user.DisplayName, pattern)
                    || (user.Bio != null && EF.Functions.ILike(user.Bio, pattern))));

        var tagsQuery = db.Tags
            .AsNoTracking()
            .Where(tag => tag.Status == TagStatuses.Active
                && (EF.Functions.ILike(tag.Name, pattern)
                    || EF.Functions.ILike(tag.Slug, pattern)));

        var counts = new SearchCountsView(
            await postsQuery.CountAsync(cancellationToken),
            await usersQuery.CountAsync(cancellationToken),
            await tagsQuery.CountAsync(cancellationToken));

        IReadOnlyList<PostSummaryView> posts = [];
        IReadOnlyList<UserSearchItemView> users = [];
        IReadOnlyList<TagSearchItemView> tags = [];
        string? nextCursor = null;

        if (normalizedType == "posts")
        {
            if (!TryDecodeSearchCursor(
                    cursor,
                    "search-posts",
                    queryHash,
                    out var cursorCreatedAt,
                    out var cursorId,
                    out var cursorRank))
            {
                return InvalidSearchResult(normalizedQuery, normalizedType, counts);
            }

            var ranked = postsQuery.Select(post => new
            {
                Post = post,
                Rank = EF.Functions.ILike(post.Title, prefix) ? 3
                    : EF.Functions.ILike(post.Title, pattern) ? 2
                    : 1
            });

            if (cursor is not null)
            {
                ranked = ranked.Where(row =>
                    row.Rank < cursorRank
                    || (row.Rank == cursorRank
                        && (row.Post.CreatedAt < cursorCreatedAt
                            || (row.Post.CreatedAt == cursorCreatedAt
                                && row.Post.Id.CompareTo(cursorId) < 0))));
            }

            var rows = await ranked
                .OrderByDescending(row => row.Rank)
                .ThenByDescending(row => row.Post.CreatedAt)
                .ThenByDescending(row => row.Post.Id)
                .Take(limit + 1)
                .Select(row => new
                {
                    row.Post,
                    row.Rank,
                    Category = db.Categories.Single(category => category.Id == row.Post.CategoryId),
                    Author = db.Users.Single(user => user.Id == row.Post.AuthorId),
                    LikeCount = db.PostLikes.Count(like => like.PostId == row.Post.Id),
                    CommentCount = db.Comments.Count(comment =>
                        comment.PostId == row.Post.Id
                        && comment.ModerationStatus == ModerationStatuses.Approved
                        && comment.DeletedAt == null),
                    ArtifactCount = db.Artifacts.Count(artifact => artifact.PostId == row.Post.Id),
                    FirstArtifactId = db.Artifacts
                        .Where(artifact => artifact.PostId == row.Post.Id)
                        .OrderBy(artifact => artifact.SortOrder)
                        .ThenBy(artifact => artifact.Id)
                        .Select(artifact => (Guid?)artifact.Id)
                        .FirstOrDefault(),
                    ActiveDeviceCount = db.Devices.Count(device =>
                        device.UserId == row.Post.AuthorId && device.Status == DeviceStatuses.Active),
                    LastVerifiedAt = db.Devices
                        .Where(device => device.UserId == row.Post.AuthorId
                            && device.Status == DeviceStatuses.Active)
                        .Max(device => (DateTimeOffset?)device.LastVerifiedAt),
                    ThumbnailUrl = db.PostImages
                        .Where(image => image.PostId == row.Post.Id)
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

            posts = pageRows
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

            nextCursor = hasMore
                ? EncodeSearchCursor(
                    "search-posts",
                    queryHash,
                    pageRows[^1].Post.CreatedAt,
                    pageRows[^1].Post.Id,
                    pageRows[^1].Rank)
                : null;
        }
        else if (normalizedType == "users")
        {
            if (!TryDecodeSearchCursor(
                    cursor,
                    "search-users",
                    queryHash,
                    out var cursorCreatedAt,
                    out var cursorId,
                    out _))
            {
                return InvalidSearchResult(normalizedQuery, normalizedType, counts);
            }

            if (cursor is not null)
            {
                usersQuery = usersQuery.Where(user =>
                    user.CreatedAt < cursorCreatedAt
                    || (user.CreatedAt == cursorCreatedAt && user.Id.CompareTo(cursorId) < 0));
            }

            var rows = await usersQuery
                .OrderByDescending(user => user.CreatedAt)
                .ThenByDescending(user => user.Id)
                .Take(limit + 1)
                .Select(user => new
                {
                    user.Id,
                    user.Handle,
                    user.DisplayName,
                    user.AvatarUrl,
                    user.TrustLevel,
                    user.CreatedAt,
                    ActiveDeviceCount = db.Devices.Count(device =>
                        device.UserId == user.Id && device.Status == DeviceStatuses.Active),
                    LastVerifiedAt = db.Devices
                        .Where(device => device.UserId == user.Id && device.Status == DeviceStatuses.Active)
                        .Max(device => (DateTimeOffset?)device.LastVerifiedAt)
                })
                .ToListAsync(cancellationToken);

            var hasMore = rows.Count > limit;
            var pageRows = hasMore ? rows.Take(limit).ToList() : rows;
            users = pageRows
                .Select(user => new UserSearchItemView(
                    user.Id,
                    user.Handle,
                    user.DisplayName,
                    user.AvatarUrl,
                    user.TrustLevel,
                    new DeviceBadgeView(
                        user.ActiveDeviceCount > 0,
                        user.ActiveDeviceCount,
                        user.LastVerifiedAt)))
                .ToList();

            nextCursor = hasMore
                ? EncodeSearchCursor(
                    "search-users",
                    queryHash,
                    pageRows[^1].CreatedAt,
                    pageRows[^1].Id,
                    rank: 0)
                : null;
        }
        else if (normalizedType == "tags")
        {
            if (!TryDecodeSearchCursor(
                    cursor,
                    "search-tags",
                    queryHash,
                    out var cursorCreatedAt,
                    out var cursorId,
                    out _))
            {
                return InvalidSearchResult(normalizedQuery, normalizedType, counts);
            }

            if (cursor is not null)
            {
                tagsQuery = tagsQuery.Where(tag =>
                    tag.CreatedAt < cursorCreatedAt
                    || (tag.CreatedAt == cursorCreatedAt && tag.Id.CompareTo(cursorId) < 0));
            }

            var tagRows = await tagsQuery
                .OrderByDescending(tag => tag.CreatedAt)
                .ThenByDescending(tag => tag.Id)
                .Take(limit + 1)
                .Select(tag => new { tag.Id, tag.Slug, tag.Name, tag.CreatedAt })
                .ToListAsync(cancellationToken);

            var hasMore = tagRows.Count > limit;
            var pageRows = hasMore ? tagRows.Take(limit).ToList() : tagRows;
            var tagIds = pageRows.Select(tag => tag.Id).ToList();
            var countsByTag = await db.PostTags
                .AsNoTracking()
                .Where(postTag => tagIds.Contains(postTag.TagId))
                .Join(
                    db.Posts.AsNoTracking(),
                    postTag => postTag.PostId,
                    post => post.Id,
                    (postTag, post) => new { postTag.TagId, post })
                .Where(row => row.post.ModerationStatus == ModerationStatuses.Approved
                    && row.post.DeletedAt == null)
                .GroupBy(row => row.TagId)
                .Select(group => new { TagId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.TagId, item => item.Count, cancellationToken);

            tags = pageRows
                .Select(tag => new TagSearchItemView(
                    tag.Slug,
                    tag.Name,
                    countsByTag.GetValueOrDefault(tag.Id)))
                .ToList();

            nextCursor = hasMore
                ? EncodeSearchCursor(
                    "search-tags",
                    queryHash,
                    pageRows[^1].CreatedAt,
                    pageRows[^1].Id,
                    rank: 0)
                : null;
        }

        return new SearchResultView(
            normalizedQuery,
            normalizedType,
            counts,
            posts,
            users,
            tags,
            nextCursor,
            InvalidQuery: false,
            InvalidCursor: false);
    }

    private static SearchResultView InvalidSearchResult(
        string query,
        string type,
        SearchCountsView counts)
    {
        return new SearchResultView(
            query,
            type,
            counts,
            [],
            [],
            [],
            null,
            InvalidQuery: false,
            InvalidCursor: true);
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static string EncodeSearchCursor(
        string scope,
        string queryHash,
        DateTimeOffset value,
        Guid id,
        int rank)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            s = scope,
            q = queryHash,
            t = value.UtcDateTime.Ticks,
            i = id,
            r = rank
        });
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeSearchCursor(
        string? cursor,
        string expectedScope,
        string expectedQueryHash,
        out DateTimeOffset value,
        out Guid id,
        out int rank)
    {
        value = default;
        id = default;
        rank = 0;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        if (cursor.Length > 512)
        {
            return false;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var payload = Convert.FromBase64String(normalized);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.GetProperty("s").GetString() != expectedScope
                || root.GetProperty("q").GetString() != expectedQueryHash)
            {
                return false;
            }

            var ticks = root.GetProperty("t").GetInt64();
            rank = root.GetProperty("r").GetInt32();
            id = root.GetProperty("i").GetGuid();
            value = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (Exception exception) when (exception is FormatException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException)
        {
            return false;
        }
    }
}