using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record CategoryView(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    int DisplayOrder,
    string Status);

public sealed record TagView(Guid Id, string Slug, string Name, int PostCount);

public sealed record TagSummaryView(string Slug, string Name);

public sealed record DeviceBadgeView(
    bool Verified,
    int ActiveDeviceCount,
    DateTimeOffset? LastVerifiedAt);

public sealed record AuthorView(
    Guid Id,
    string Handle,
    string DisplayName,
    string? AvatarUrl,
    short TrustLevel,
    DeviceBadgeView Badge);

public sealed record EnvironmentView(string? KernelVersion, string? Platform, string? Model);

public sealed record PostCountsView(int Views, int Copies, int Likes, int Comments, int Artifacts);

public sealed record PostSummaryView(
    Guid Id,
    string Title,
    string Excerpt,
    CategoryView Category,
    AuthorView Author,
    IReadOnlyList<TagSummaryView> Tags,
    string ModerationStatus,
    bool IsSolved,
    Guid? AcceptedCommentId,
    [property: JsonIgnore] Guid? FirstArtifactId,
    EnvironmentView Environment,
    PostCountsView Counts,
    string? ThumbnailUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

public sealed record PostImageView(
    Guid Id,
    string Url,
    int Width,
    int Height,
    string? AltText,
    string MimeType,
    short SortOrder);

public sealed record ArtifactView(
    Guid Id,
    string Kind,
    string Name,
    string? Note,
    string Content,
    int CopyCount,
    short SortOrder);

public sealed record PostDetailView(
    Guid Id,
    string Title,
    string Excerpt,
    CategoryView Category,
    AuthorView Author,
    IReadOnlyList<TagSummaryView> Tags,
    string ModerationStatus,
    bool IsSolved,
    Guid? AcceptedCommentId,
    EnvironmentView Environment,
    PostCountsView Counts,
    string? ThumbnailUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string BodyMd,
    string BodyHtml,
    IReadOnlyList<PostImageView> Images,
    IReadOnlyList<ArtifactView> Artifacts,
    DateTimeOffset UpdatedAt,
    int Version,
    string? LatestModerationReason,
    DateTimeOffset? LatestModerationAt);

public sealed record FeedPage(
    IReadOnlyList<PostSummaryView> Items,
    string? NextCursor,
    bool InvalidCursor);

public sealed record TagPage(
    IReadOnlyList<TagView> Items,
    string? NextCursor,
    bool InvalidCursor);

public sealed record TagDetailView(TagView Tag, FeedPage Posts, string Note);

public sealed record UserStatsView(int Posts, int Comments, int AcceptedAnswers, int LikesReceived);

public sealed record UserProfileView(
    Guid Id,
    string Handle,
    string DisplayName,
    string? Bio,
    string? AvatarUrl,
    DateTimeOffset JoinedAt,
    short TrustLevel,
    DeviceBadgeView Badge,
    UserStatsView Stats,
    string Note);

public sealed record CommentView(
    Guid Id,
    Guid PostId,
    Guid? ParentId,
    AuthorView Author,
    string? BodyMd,
    string? BodyHtml,
    string ModerationStatus,
    bool IsAccepted,
    int LikeCount,
    bool IsDeleted,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CommentPage(
    IReadOnlyList<CommentView> Items,
    string? NextCursor,
    bool InvalidCursor);

public sealed record SearchCountsView(int Posts, int Users, int Tags);

public sealed record UserSearchItemView(
    Guid Id,
    string Handle,
    string DisplayName,
    string? AvatarUrl,
    short TrustLevel,
    DeviceBadgeView Badge);

public sealed record TagSearchItemView(string Slug, string Name, int PostCount);

public sealed record SearchResultView(
    string Query,
    string Type,
    SearchCountsView Counts,
    IReadOnlyList<PostSummaryView> Posts,
    IReadOnlyList<UserSearchItemView> Users,
    IReadOnlyList<TagSearchItemView> Tags,
    string? NextCursor,
    bool InvalidQuery,
    bool InvalidCursor);

public sealed class PublicContentService(WhalePodDbContext db)
{
    private static readonly string[] FeedSorts = ["latest", "active", "unresolved"];

    public async Task<IReadOnlyList<CategoryView>> GetCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .Where(category => category.Status == CategoryStatuses.Active)
            .OrderBy(category => category.DisplayOrder)
            .ThenBy(category => category.Name)
            .Select(category => new
            {
                category.Id,
                category.Slug,
                category.Name,
                category.Description,
                category.DisplayOrder,
                category.Status
            })
            .ToListAsync(cancellationToken);

        return categories
            .Select(category => new CategoryView(
                category.Id,
                category.Slug,
                category.Name,
                category.Description,
                category.DisplayOrder,
                category.Status))
            .ToList();
    }

    public async Task<TagPage> GetTagsAsync(
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var query = db.Tags
            .AsNoTracking()
            .Where(tag => tag.Status == TagStatuses.Active);

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, "tags", out var cursorCreatedAt, out var cursorId))
            {
                return new TagPage([], null, true);
            }

            query = query.Where(tag =>
                tag.CreatedAt < cursorCreatedAt
                || (tag.CreatedAt == cursorCreatedAt && tag.Id.CompareTo(cursorId) < 0));
        }

        var tagRows = await query
            .OrderByDescending(tag => tag.CreatedAt)
            .ThenByDescending(tag => tag.Id)
            .Take(limit + 1)
            .Select(tag => new { tag.Id, tag.Slug, tag.Name, tag.CreatedAt })
            .ToListAsync(cancellationToken);

        var hasMore = tagRows.Count > limit;
        var pageRows = hasMore ? tagRows.Take(limit).ToList() : tagRows;
        var tagIds = pageRows.Select(tag => tag.Id).ToList();

        var counts = await db.PostTags
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

        var items = pageRows
            .Select(tag => new TagView(
                tag.Id,
                tag.Slug,
                tag.Name,
                counts.GetValueOrDefault(tag.Id)))
            .ToList();

        var nextCursor = hasMore
            ? EncodeCursor("tags", pageRows[^1].CreatedAt, pageRows[^1].Id)
            : null;

        return new TagPage(items, nextCursor, false);
    }

    public Task<FeedPage> GetFeedAsync(
        string? sort,
        string? categorySlug,
        string? tagSlug,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        return GetFeedCoreAsync(
            sort,
            categorySlug,
            tagSlug,
            null,
            limit,
            cursor,
            cancellationToken);
    }

    public async Task<TagDetailView?> GetTagDetailAsync(
        string slug,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedSlug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedSlug.Length == 0)
        {
            return null;
        }

        var tag = await db.Tags
            .AsNoTracking()
            .Where(candidate => candidate.Slug == normalizedSlug && candidate.Status == TagStatuses.Active)
            .Select(candidate => new { candidate.Id, candidate.Slug, candidate.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (tag is null)
        {
            return null;
        }

        var postCount = await db.PostTags
            .AsNoTracking()
            .Where(postTag => postTag.TagId == tag.Id)
            .Join(
                db.Posts.AsNoTracking(),
                postTag => postTag.PostId,
                post => post.Id,
                (postTag, post) => post)
            .CountAsync(post => post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null, cancellationToken);

        var posts = await GetFeedCoreAsync(
            "latest",
            null,
            normalizedSlug,
            null,
            limit,
            cursor,
            cancellationToken);

        return new TagDetailView(
            new TagView(tag.Id, tag.Slug, tag.Name, postCount),
            posts,
            "标签页只聚合公开帖子，隐藏标签不返回。");
    }

    public async Task<UserProfileView?> GetUserProfileAsync(
        string handle,
        CancellationToken cancellationToken = default)
    {
        var normalizedHandle = (handle ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedHandle.Length is < 3 or > 32)
        {
            return null;
        }

        var user = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Handle == normalizedHandle
                && (candidate.Status == UserStatuses.Active || candidate.Status == UserStatuses.Muted)
                && candidate.ActivatedAt != null
                && candidate.DeletedAt == null)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Handle,
                candidate.DisplayName,
                candidate.Bio,
                candidate.AvatarUrl,
                candidate.CreatedAt,
                candidate.TrustLevel
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return null;
        }

        var postCount = await db.Posts
            .AsNoTracking()
            .CountAsync(post => post.AuthorId == user.Id
                && post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null, cancellationToken);
        var commentCount = await db.Comments
            .AsNoTracking()
            .CountAsync(comment => comment.AuthorId == user.Id
                && comment.ModerationStatus == ModerationStatuses.Approved
                && comment.DeletedAt == null, cancellationToken);
        var acceptedAnswers = await db.Comments
            .AsNoTracking()
            .CountAsync(comment => comment.AuthorId == user.Id
                && comment.IsAccepted
                && comment.ModerationStatus == ModerationStatuses.Approved
                && comment.DeletedAt == null, cancellationToken);
        var postLikes = await db.PostLikes
            .AsNoTracking()
            .Where(like => db.Posts.Any(post => post.Id == like.PostId
                && post.AuthorId == user.Id
                && post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null))
            .CountAsync(cancellationToken);
        var commentLikes = await db.CommentLikes
            .AsNoTracking()
            .Where(like => db.Comments.Any(comment => comment.Id == like.CommentId
                && comment.AuthorId == user.Id
                && comment.ModerationStatus == ModerationStatuses.Approved
                && comment.DeletedAt == null))
            .CountAsync(cancellationToken);
        var activeDeviceCount = await db.Devices
            .AsNoTracking()
            .CountAsync(device => device.UserId == user.Id && device.Status == DeviceStatuses.Active, cancellationToken);
        var lastVerifiedAt = await db.Devices
            .AsNoTracking()
            .Where(device => device.UserId == user.Id && device.Status == DeviceStatuses.Active)
            .MaxAsync(device => (DateTimeOffset?)device.LastVerifiedAt, cancellationToken);

        return new UserProfileView(
            user.Id,
            user.Handle,
            user.DisplayName,
            user.Bio,
            user.AvatarUrl,
            user.CreatedAt,
            user.TrustLevel,
            new DeviceBadgeView(activeDeviceCount > 0, activeDeviceCount, lastVerifiedAt),
            new UserStatsView(postCount, commentCount, acceptedAnswers, postLikes + commentLikes),
            "信任等级反映社区信任，设备徽章仅证明密钥持有，不代表内容一定正确。");
    }

    public async Task<FeedPage?> GetUserPostsAsync(
        string handle,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedHandle = (handle ?? string.Empty).Trim().ToLowerInvariant();
        var userId = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Handle == normalizedHandle
                && (candidate.Status == UserStatuses.Active || candidate.Status == UserStatuses.Muted)
                && candidate.ActivatedAt != null
                && candidate.DeletedAt == null)
            .Select(candidate => (Guid?)candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            return null;
        }

        return await GetFeedCoreAsync(
            "latest",
            null,
            null,
            userId,
            limit,
            cursor,
            cancellationToken);
    }

    public async Task<CommentPage?> GetCommentsAsync(
        Guid postId,
        string? sort,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var postExists = await db.Posts
            .AsNoTracking()
            .AnyAsync(post => post.Id == postId
                && post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null, cancellationToken);
        if (!postExists)
        {
            return null;
        }

        var normalizedSort = string.IsNullOrWhiteSpace(sort) ? "latest" : sort.Trim().ToLowerInvariant();
        if (normalizedSort is not ("latest" or "top"))
        {
            return new CommentPage([], null, false);
        }

        var query = db.Comments
            .AsNoTracking()
            .Where(comment => comment.PostId == postId
                && ((comment.DeletedAt == null
                        && comment.ModerationStatus == ModerationStatuses.Approved)
                    || (comment.DeletedAt != null
                        && db.Comments.Any(child => child.ParentId == comment.Id
                            && child.ModerationStatus == ModerationStatuses.Approved
                            && child.DeletedAt == null))));

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCommentCursor(cursor, normalizedSort, out var cursorCreatedAt, out var cursorLikeCount, out var cursorId))
            {
                return new CommentPage([], null, true);
            }

            query = normalizedSort == "top"
                ? query.Where(comment =>
                    comment.LikeCount < cursorLikeCount
                    || (comment.LikeCount == cursorLikeCount
                        && (comment.CreatedAt < cursorCreatedAt
                            || (comment.CreatedAt == cursorCreatedAt && comment.Id.CompareTo(cursorId) < 0))))
                : query.Where(comment =>
                    comment.CreatedAt < cursorCreatedAt
                    || (comment.CreatedAt == cursorCreatedAt && comment.Id.CompareTo(cursorId) < 0));
        }

        var ordered = normalizedSort == "top"
            ? query.OrderByDescending(comment => comment.LikeCount)
                .ThenByDescending(comment => comment.CreatedAt)
                .ThenByDescending(comment => comment.Id)
            : query.OrderByDescending(comment => comment.CreatedAt)
                .ThenByDescending(comment => comment.Id);

        var rows = await ordered
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
                ToAuthor(row.Author, row.ActiveDeviceCount, row.LastVerifiedAt),
                row.Comment.DeletedAt is null ? row.Comment.BodyMd : null,
                row.Comment.DeletedAt is null ? row.Comment.BodyHtml : null,
                row.Comment.ModerationStatus,
                row.Comment.IsAccepted,
                row.Comment.LikeCount,
                row.Comment.DeletedAt is not null,
                row.Comment.Version,
                row.Comment.CreatedAt,
                row.Comment.UpdatedAt))
            .ToList();

        var nextCursor = hasMore
            ? EncodeCommentCursor(
                normalizedSort,
                pageRows[^1].Comment.CreatedAt,
                pageRows[^1].Comment.LikeCount,
                pageRows[^1].Comment.Id)
            : null;
        return new CommentPage(items, nextCursor, false);
    }

    public async Task<CommentView?> GetCommentForViewerAsync(
        Guid commentId,
        Guid? viewerId,
        bool viewerIsAdmin,
        CancellationToken cancellationToken = default)
    {
        var effectiveViewerId = await ResolveEffectiveViewerAsync(
            viewerId,
            viewerIsAdmin,
            cancellationToken);
        var row = await db.Comments
            .AsNoTracking()
            .Where(comment => comment.Id == commentId && comment.DeletedAt == null)
            .Where(comment => viewerIsAdmin
                || comment.ModerationStatus == ModerationStatuses.Approved
                || (effectiveViewerId != null
                    && comment.AuthorId == effectiveViewerId
                    && (comment.ModerationStatus == ModerationStatuses.Pending
                        || comment.ModerationStatus == ModerationStatuses.Rejected)))
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
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new CommentView(
            row.Comment.Id,
            row.Comment.PostId,
            row.Comment.ParentId,
            ToAuthor(row.Author, row.ActiveDeviceCount, row.LastVerifiedAt),
            row.Comment.BodyMd,
            row.Comment.BodyHtml,
            row.Comment.ModerationStatus,
            row.Comment.IsAccepted,
            row.Comment.LikeCount,
            false,
            row.Comment.Version,
            row.Comment.CreatedAt,
            row.Comment.UpdatedAt);
    }

    private async Task<FeedPage> GetFeedCoreAsync(
        string? sort,
        string? categorySlug,
        string? tagSlug,
        Guid? authorId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedSort = string.IsNullOrWhiteSpace(sort) ? "latest" : sort.Trim().ToLowerInvariant();
        if (!FeedSorts.Contains(normalizedSort, StringComparer.Ordinal))
        {
            return new FeedPage([], null, false);
        }

        var query = db.Posts
            .AsNoTracking()
            .Where(post => post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(categorySlug))
        {
            var slug = categorySlug.Trim().ToLowerInvariant();
            query = query.Where(post => db.Categories.Any(category =>
                category.Id == post.CategoryId && category.Slug == slug));
        }

        if (!string.IsNullOrWhiteSpace(tagSlug))
        {
            var slug = tagSlug.Trim().ToLowerInvariant();
            query = query.Where(post => db.PostTags.Any(postTag =>
                postTag.PostId == post.Id
                && db.Tags.Any(tag => tag.Id == postTag.TagId
                    && tag.Slug == slug
                    && tag.Status == TagStatuses.Active)));
        }

        if (normalizedSort == "unresolved")
        {
            query = query.Where(post => !post.IsSolved);
        }

        if (authorId is not null)
        {
            query = query.Where(post => post.AuthorId == authorId);
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, normalizedSort, out var cursorSortValue, out var cursorId))
            {
                return new FeedPage([], null, true);
            }

            query = normalizedSort == "active"
                ? query.Where(post =>
                    post.LastActivityAt < cursorSortValue
                    || (post.LastActivityAt == cursorSortValue && post.Id.CompareTo(cursorId) < 0))
                : query.Where(post =>
                    post.CreatedAt < cursorSortValue
                    || (post.CreatedAt == cursorSortValue && post.Id.CompareTo(cursorId) < 0));
        }

        var ordered = normalizedSort == "active"
            ? query.OrderByDescending(post => post.LastActivityAt).ThenByDescending(post => post.Id)
            : query.OrderByDescending(post => post.CreatedAt).ThenByDescending(post => post.Id);

        var rows = await ordered
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
            .Select(row => ToSummary(
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
            ? EncodeCursor(
                normalizedSort,
                normalizedSort == "active" ? pageRows[^1].Post.LastActivityAt : pageRows[^1].Post.CreatedAt,
                pageRows[^1].Post.Id)
            : null;

        return new FeedPage(items, nextCursor, false);
    }

    public Task<PostDetailView?> GetPostAsync(
        Guid postId,
        CancellationToken cancellationToken = default)
    {
        return GetPostForViewerAsync(postId, null, false, cancellationToken);
    }

    public async Task<PostDetailView?> GetPostForViewerAsync(
        Guid postId,
        Guid? viewerId,
        bool viewerIsAdmin,
        CancellationToken cancellationToken = default)
    {
        var effectiveViewerId = await ResolveEffectiveViewerAsync(
            viewerId,
            viewerIsAdmin,
            cancellationToken);
        var visible = db.Posts
            .AsNoTracking()
            .Where(post => post.Id == postId && post.DeletedAt == null);

        if (!viewerIsAdmin)
        {
            visible = effectiveViewerId is null
                ? visible.Where(post => post.ModerationStatus == ModerationStatuses.Approved)
                : visible.Where(post =>
                    post.ModerationStatus == ModerationStatuses.Approved
                    || (post.AuthorId == effectiveViewerId
                        && (post.ModerationStatus == ModerationStatuses.Pending
                            || post.ModerationStatus == ModerationStatuses.Rejected)));
        }

        var row = await visible
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
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var tagRows = await (
                from postTag in db.PostTags.AsNoTracking()
                join tag in db.Tags.AsNoTracking() on postTag.TagId equals tag.Id
                where postTag.PostId == postId && tag.Status == TagStatuses.Active
                orderby tag.Name
                select new { tag.Slug, tag.Name })
            .ToListAsync(cancellationToken);

        var tags = tagRows
            .Select(tag => new TagSummaryView(tag.Slug, tag.Name))
            .ToList();

        var imageRows = await db.PostImages
            .AsNoTracking()
            .Where(image => image.PostId == postId)
            .OrderBy(image => image.SortOrder)
            .ThenBy(image => image.Id)
            .Select(image => new
            {
                image.Id,
                image.Url,
                image.Width,
                image.Height,
                image.AltText,
                image.MimeType,
                image.SortOrder
            })
            .ToListAsync(cancellationToken);

        var images = imageRows
            .Select(image => new PostImageView(
                image.Id,
                image.Url,
                image.Width,
                image.Height,
                image.AltText,
                image.MimeType,
                image.SortOrder))
            .ToList();

        var artifactRows = await db.Artifacts
            .AsNoTracking()
            .Where(artifact => artifact.PostId == postId)
            .OrderBy(artifact => artifact.SortOrder)
            .ThenBy(artifact => artifact.Id)
            .Select(artifact => new
            {
                artifact.Id,
                artifact.Kind,
                artifact.Name,
                artifact.Note,
                artifact.Content,
                artifact.CopyCount,
                artifact.SortOrder
            })
            .ToListAsync(cancellationToken);

        var artifacts = artifactRows
            .Select(artifact => new ArtifactView(
                artifact.Id,
                artifact.Kind,
                artifact.Name,
                artifact.Note,
                artifact.Content,
                artifact.CopyCount,
                artifact.SortOrder))
            .ToList();

        var post = row.Post;
        var category = row.Category;
        var author = row.Author;
        string? latestModerationReason = null;
        DateTimeOffset? latestModerationAt = null;
        if ((viewerIsAdmin || effectiveViewerId == post.AuthorId)
            && post.ModerationStatus != ModerationStatuses.Approved)
        {
            var latestAction = await db.ModerationActions
                .AsNoTracking()
                .Where(action => action.TargetType == "post"
                    && action.TargetId == post.Id
                    && action.Reason != null)
                .OrderByDescending(action => action.CreatedAt)
                .ThenByDescending(action => action.Id)
                .Select(action => new { action.Reason, action.CreatedAt })
                .FirstOrDefaultAsync(cancellationToken);
            latestModerationReason = latestAction?.Reason;
            latestModerationAt = latestAction?.CreatedAt;
        }

        return new PostDetailView(
            post.Id,
            post.Title,
            post.Excerpt,
            ToCategory(category),
            ToAuthor(author, row.ActiveDeviceCount, row.LastVerifiedAt),
            tags,
            post.ModerationStatus,
            post.IsSolved,
            post.AcceptedCommentId,
            ToEnvironment(post),
            new PostCountsView(
                post.ViewCount,
                post.CopyCount,
                row.LikeCount,
                row.CommentCount,
                row.ArtifactCount),
            row.ThumbnailUrl,
            post.CreatedAt,
            post.LastActivityAt,
            post.BodyMd,
            post.BodyHtml,
            images,
            artifacts,
            post.UpdatedAt,
            post.Version,
            latestModerationReason,
            latestModerationAt);
    }

    internal static PostSummaryView ToSummary(
        Post post,
        Category category,
        User author,
        int likeCount,
        int commentCount,
        int artifactCount,
        Guid? firstArtifactId,
        int activeDeviceCount,
        DateTimeOffset? lastVerifiedAt,
        string? thumbnailUrl,
        IReadOnlyList<TagSummaryView> tags)
    {
        return new PostSummaryView(
            post.Id,
            post.Title,
            post.Excerpt,
            ToCategory(category),
            ToAuthor(author, activeDeviceCount, lastVerifiedAt),
            tags,
            post.ModerationStatus,
            post.IsSolved,
            post.AcceptedCommentId,
            firstArtifactId,
            ToEnvironment(post),
            new PostCountsView(
                post.ViewCount,
                post.CopyCount,
                likeCount,
                commentCount,
                artifactCount),
            thumbnailUrl,
            post.CreatedAt,
            post.LastActivityAt);
    }

    internal static CategoryView ToCategory(Category category)
    {
        return new CategoryView(
            category.Id,
            category.Slug,
            category.Name,
            category.Description,
            category.DisplayOrder,
            category.Status);
    }

    internal static AuthorView ToAuthor(
        User author,
        int activeDeviceCount,
        DateTimeOffset? lastVerifiedAt)
    {
        return new AuthorView(
            author.Id,
            author.Handle,
            author.DisplayName,
            author.AvatarUrl,
            author.TrustLevel,
            new DeviceBadgeView(activeDeviceCount > 0, activeDeviceCount, lastVerifiedAt));
    }

    internal static EnvironmentView ToEnvironment(Post post)
    {
        return new EnvironmentView(post.EnvKernel, post.EnvPlatform, post.EnvModel);
    }

    private async Task<Guid?> ResolveEffectiveViewerAsync(
        Guid? viewerId,
        bool viewerIsAdmin,
        CancellationToken cancellationToken)
    {
        if (viewerId is null || viewerIsAdmin)
        {
            return viewerId;
        }

        var viewer = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == viewerId)
            .Select(user => new { user.Status, user.DeletedAt })
            .SingleOrDefaultAsync(cancellationToken);
        return viewer is null
            || viewer.DeletedAt is not null
            || viewer.Status is UserStatuses.Banned or UserStatuses.Deleted
                ? null
                : viewerId;
    }

    private static bool TryDecodeCursor(
        string cursor,
        string expectedSort,
        out DateTimeOffset value,
        out Guid id)
    {
        value = default;
        id = default;
        if (cursor.Length is < 1 or > 512)
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
            if (root.GetProperty("s").GetString() != expectedSort)
            {
                return false;
            }

            var ticks = root.GetProperty("t").GetInt64();
            id = root.GetProperty("i").GetGuid();
            if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

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


    private static string EncodeCommentCursor(
        string sort,
        DateTimeOffset value,
        int likeCount,
        Guid id)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            s = sort,
            t = value.UtcDateTime.Ticks,
            l = likeCount,
            i = id
        });
        return ToBase64Url(payload);
    }

    private static bool TryDecodeCommentCursor(
        string cursor,
        string expectedSort,
        out DateTimeOffset value,
        out int likeCount,
        out Guid id)
    {
        value = default;
        likeCount = 0;
        id = default;
        if (cursor.Length is < 1 or > 512)
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
            if (root.GetProperty("s").GetString() != expectedSort)
            {
                return false;
            }

            var ticks = root.GetProperty("t").GetInt64();
            likeCount = root.GetProperty("l").GetInt32();
            id = root.GetProperty("i").GetGuid();
            if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

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

    private static string EncodeCursor(string sort, DateTimeOffset value, Guid id)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            s = sort,
            t = value.UtcDateTime.Ticks,
            i = id
        });
        return ToBase64Url(payload);
    }

    private static string ToBase64Url(byte[] payload)
    {
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}