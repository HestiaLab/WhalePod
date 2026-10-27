using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record SitemapEntry(string Path, DateTimeOffset LastModified);

public sealed record SitemapDocument(
    IReadOnlyList<SitemapEntry> Entries,
    DateTimeOffset LastModified);

public sealed record RssItemView(
    Guid Id,
    string Title,
    string Excerpt,
    string AuthorHandle,
    string CategorySlug,
    DateTimeOffset CreatedAt);

public sealed record RssDocument(
    IReadOnlyList<RssItemView> Items,
    DateTimeOffset LastModified);

public sealed class PublicDiscoveryService(WhalePodDbContext db)
{
    public async Task<SitemapDocument> GetSitemapAsync(
        int maxPosts = 5000,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<SitemapEntry>();
        var postLimit = Math.Clamp(maxPosts, 1, 10_000);

        var posts = await db.Posts
            .AsNoTracking()
            .Where(post => post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null)
            .OrderByDescending(post => post.UpdatedAt)
            .ThenByDescending(post => post.Id)
            .Take(postLimit)
            .Select(post => new { post.Id, post.UpdatedAt })
            .ToListAsync(cancellationToken);
        entries.AddRange(posts.Select(post =>
            new SitemapEntry($"/posts/{post.Id}", post.UpdatedAt)));

        var categories = await db.Categories
            .AsNoTracking()
            .Where(category => category.Status == CategoryStatuses.Active)
            .OrderBy(category => category.DisplayOrder)
            .ThenBy(category => category.Name)
            .Select(category => new { category.Slug, category.UpdatedAt })
            .ToListAsync(cancellationToken);
        entries.AddRange(categories.Select(category =>
            new SitemapEntry($"/?category={Uri.EscapeDataString(category.Slug)}", category.UpdatedAt)));

        var users = await db.Users
            .AsNoTracking()
            .Where(user => (user.Status == UserStatuses.Active || user.Status == UserStatuses.Muted)
                && user.ActivatedAt != null
                && user.DeletedAt == null)
            .OrderBy(user => user.Handle)
            .Select(user => new { user.Handle, user.UpdatedAt })
            .ToListAsync(cancellationToken);
        entries.AddRange(users.Select(user =>
            new SitemapEntry($"/users/{user.Handle}", user.UpdatedAt)));

        var tags = await db.Tags
            .AsNoTracking()
            .Where(tag => tag.Status == TagStatuses.Active)
            .OrderBy(tag => tag.Slug)
            .Select(tag => new { tag.Slug, tag.UpdatedAt })
            .ToListAsync(cancellationToken);
        entries.AddRange(tags.Select(tag =>
            new SitemapEntry($"/tags/{tag.Slug}", tag.UpdatedAt)));

        var latestPost = posts.Count == 0
            ? DateTimeOffset.UnixEpoch
            : posts.Max(post => post.UpdatedAt);
        var latestTag = tags.Count == 0
            ? DateTimeOffset.UnixEpoch
            : tags.Max(tag => tag.UpdatedAt);
        entries.Add(new SitemapEntry("/", latestPost));
        entries.Add(new SitemapEntry("/tags", latestTag));

        var lastModified = entries.Count == 0
            ? DateTimeOffset.UnixEpoch
            : entries.Max(entry => entry.LastModified);
        return new SitemapDocument(entries, lastModified);
    }

    public async Task<RssDocument> GetRssAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 200);
        var rows = await (
                from post in db.Posts.AsNoTracking()
                join author in db.Users.AsNoTracking() on post.AuthorId equals author.Id
                join category in db.Categories.AsNoTracking() on post.CategoryId equals category.Id
                where post.ModerationStatus == ModerationStatuses.Approved
                    && post.DeletedAt == null
                orderby post.CreatedAt descending, post.Id descending
                select new
                {
                    post.Id,
                    post.Title,
                    post.Excerpt,
                    author.Handle,
                    category.Slug,
                    post.CreatedAt,
                    post.UpdatedAt
                })
            .Take(take)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new RssItemView(
                row.Id,
                row.Title,
                row.Excerpt,
                row.Handle,
                row.Slug,
                row.CreatedAt))
            .ToList();
        var lastModified = rows.Count == 0
            ? DateTimeOffset.UnixEpoch
            : rows.Max(row => row.UpdatedAt);
        return new RssDocument(items, lastModified);
    }
}