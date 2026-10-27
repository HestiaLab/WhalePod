using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class PublicSearchServiceTests
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("WHALEPOD_TEST_DB_CONNECTION")
        ?? throw new InvalidOperationException("WHALEPOD_TEST_DB_CONNECTION is required.");

    private static WhalePodDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WhalePodDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WhalePodDbContext(options);
    }

    [DatabaseFact]
    public async Task Search_returns_grouped_counts_and_only_public_posts()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var data = await SeedSearchAsync(db);
        var service = new PublicSearchService(db);

        var posts = await service.SearchAsync(data.Term, "posts", 10, null);
        Assert.False(posts.InvalidQuery);
        Assert.False(posts.InvalidCursor);
        Assert.Equal(new SearchCountsView(3, 1, 1), posts.Counts);
        Assert.Equal(
            new[] { data.TitleMatch, data.BodyMatch, data.TagMatch },
            posts.Posts.Select(post => post.Id));
        Assert.DoesNotContain(posts.Posts, post => post.Id == data.PendingPost || post.Id == data.DeletedPost);

        var users = await service.SearchAsync(data.Term, "users", 10, null);
        var user = Assert.Single(users.Users);
        Assert.Equal(data.AuthorHandle, user.Handle);
        Assert.True(user.Badge.Verified);
        Assert.Equal(1, user.Badge.ActiveDeviceCount);

        var tags = await service.SearchAsync(data.Term, "tags", 10, null);
        var tag = Assert.Single(tags.Tags);
        Assert.Equal(data.TagSlug, tag.Slug);
        Assert.Equal(1, tag.PostCount);

        var upper = await service.SearchAsync(data.Term.ToUpperInvariant(), "posts", 10, null);
        Assert.Equal(3, upper.Counts.Posts);
        Assert.Equal(
            new[] { data.TitleMatch, data.BodyMatch, data.TagMatch },
            upper.Posts.Select(post => post.Id));
    }

    [DatabaseFact]
    public async Task Search_paginates_with_scope_bound_cursor_and_rejects_empty_query()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var data = await SeedSearchAsync(db);
        var service = new PublicSearchService(db);

        var firstPage = await service.SearchAsync(data.Term, "posts", 1, null);
        var first = Assert.Single(firstPage.Posts);
        Assert.Equal(data.TitleMatch, first.Id);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await service.SearchAsync(data.Term, "posts", 1, firstPage.NextCursor);
        Assert.Equal(data.BodyMatch, Assert.Single(secondPage.Posts).Id);

        var wrongQuery = await service.SearchAsync(data.Term, "posts", 1, firstPage.NextCursor);
        Assert.False(wrongQuery.InvalidCursor);

        var otherQuery = await service.SearchAsync($"{data.Term}-other", "posts", 1, firstPage.NextCursor);
        Assert.True(otherQuery.InvalidCursor);

        var wrongType = await service.SearchAsync(data.Term, "users", 1, firstPage.NextCursor);
        Assert.True(wrongType.InvalidCursor);

        var empty = await service.SearchAsync("   ", "posts", 10, null);
        Assert.True(empty.InvalidQuery);
        Assert.Empty(empty.Posts);
    }

    private static async Task<SearchSeed> SeedSearchAsync(WhalePodDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var term = $"srch{suffix}";
        var now = DateTimeOffset.UtcNow;

        var author = new User
        {
            Handle = $"search-{term}",
            DisplayName = $"Search User {term}",
            Status = UserStatuses.Active,
            Role = UserRoles.Member,
            ActivatedAt = now
        };
        var category = new Category
        {
            Slug = $"search-{suffix}",
            Name = "Search Test",
            Status = CategoryStatuses.Active
        };
        var activeTag = new Tag
        {
            Slug = $"search-tag-{term}",
            Name = $"Search Tag {term}",
            Status = TagStatuses.Active
        };
        var hiddenTag = new Tag
        {
            Slug = $"search-hidden-{term}",
            Name = $"Hidden Tag {term}",
            Status = TagStatuses.Hidden
        };

        var titleMatch = NewPost(author.Id, category.Id, $" {term} title match post", now.AddHours(-1));
        titleMatch.Title = $"{term} title match post";
        titleMatch.BodyMd = "This body does not contain the token.";
        titleMatch.BodyHtml = "<p>This body does not contain the token.</p>";
        titleMatch.Excerpt = "title match excerpt";

        var bodyMatch = NewPost(author.Id, category.Id, "Body match post", now.AddHours(-2));
        bodyMatch.BodyMd = $"Body with {term} token.";
        bodyMatch.BodyHtml = $"<p>Body with {term} token.</p>";

        var tagMatch = NewPost(author.Id, category.Id, "Tag match post", now.AddHours(-3));
        tagMatch.BodyMd = "Tag-only match body.";
        tagMatch.BodyHtml = "<p>Tag-only match body.</p>";

        var pending = NewPost(author.Id, category.Id, $"{term} pending post", now.AddMinutes(-30));
        pending.ModerationStatus = ModerationStatuses.Pending;
        var deleted = NewPost(author.Id, category.Id, $"{term} deleted post", now.AddMinutes(-20));
        deleted.DeletedAt = now.AddMinutes(-5);

        db.Users.Add(author);
        db.Categories.Add(category);
        db.Tags.AddRange(activeTag, hiddenTag);
        db.Posts.AddRange(titleMatch, bodyMatch, tagMatch, pending, deleted);
        db.PostTags.AddRange(
            new PostTag { PostId = tagMatch.Id, TagId = activeTag.Id },
            new PostTag { PostId = bodyMatch.Id, TagId = hiddenTag.Id });
        db.Devices.Add(NewDevice(author.Id, now));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new SearchSeed(
            term,
            author.Handle,
            activeTag.Slug,
            titleMatch.Id,
            bodyMatch.Id,
            tagMatch.Id,
            pending.Id,
            deleted.Id);
    }

    private static Post NewPost(Guid authorId, Guid categoryId, string title, DateTimeOffset createdAt)
    {
        return new Post
        {
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = title,
            BodyMd = "This is a searchable body.",
            BodyHtml = "<p>This is a searchable body.</p>",
            Excerpt = "search excerpt",
            ModerationStatus = ModerationStatuses.Approved,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActivityAt = createdAt
        };
    }

    private static Device NewDevice(Guid userId, DateTimeOffset now)
    {
        return new Device
        {
            UserId = userId,
            PublicKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            KernelVersion = "6.8.0-45-generic",
            Platform = Platforms.LinuxX64,
            Status = DeviceStatuses.Active,
            CreatedAt = now,
            LastVerifiedAt = now
        };
    }

    private sealed record SearchSeed(
        string Term,
        string AuthorHandle,
        string TagSlug,
        Guid TitleMatch,
        Guid BodyMatch,
        Guid TagMatch,
        Guid PendingPost,
        Guid DeletedPost);
}