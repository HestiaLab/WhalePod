using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class CopyCounterServiceTests
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
    public async Task Copy_is_counted_once_per_ip_and_bucket()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var (post, artifact) = await SeedApprovedArtifactAsync(db);

        var service = new CopyCounterService(
            db,
            Options.Create(new CopyCounterOptions
            {
                DedupeSecret = "test-copy-counter-secret",
                BucketSeconds = 600,
                DedupeTtlSeconds = 720
            }),
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)));

        Assert.True(await service.RecordCopyAsync(artifact.Id, "192.0.2.10"));
        Assert.False(await service.RecordCopyAsync(artifact.Id, "192.0.2.10"));
        Assert.True(await service.RecordCopyAsync(artifact.Id, "192.0.2.11"));

        db.ChangeTracker.Clear();
        Assert.Equal(2, (await db.Artifacts.SingleAsync(candidate => candidate.Id == artifact.Id)).CopyCount);
        Assert.Equal(2, (await db.Posts.SingleAsync(candidate => candidate.Id == post.Id)).CopyCount);
    }

    [DatabaseFact]
    public async Task Copy_is_not_counted_for_unapproved_post()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var (_, artifact) = await SeedApprovedArtifactAsync(db, approved: false);

        var service = new CopyCounterService(
            db,
            Options.Create(new CopyCounterOptions
            {
                DedupeSecret = "test-copy-counter-secret",
                BucketSeconds = 600,
                DedupeTtlSeconds = 720
            }),
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)));

        Assert.False(await service.RecordCopyAsync(artifact.Id, "192.0.2.12"));

        db.ChangeTracker.Clear();
        Assert.Equal(0, (await db.Artifacts.SingleAsync(candidate => candidate.Id == artifact.Id)).CopyCount);
    }

    private static async Task<(Post Post, Artifact Artifact)> SeedApprovedArtifactAsync(WhalePodDbContext db, bool approved = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var author = new User
        {
            Handle = $"copy-{suffix}",
            DisplayName = "测试用户",
            Status = UserStatuses.Active,
            Role = UserRoles.Member
        };
        var category = new Category
        {
            Slug = $"copy-cat-{suffix}",
            Name = "测试板块",
            Status = CategoryStatuses.Active
        };
        var post = new Post
        {
            AuthorId = author.Id,
            CategoryId = category.Id,
            Title = "复制计数测试帖子",
            BodyMd = "这是一个足够长的复制计数测试正文内容。",
            BodyHtml = "<p>这是一个足够长的复制计数测试正文内容。</p>",
            Excerpt = "复制计数测试",
            ModerationStatus = approved ? ModerationStatuses.Approved : ModerationStatuses.Pending
        };
        var artifact = new Artifact
        {
            PostId = post.Id,
            Kind = ArtifactKinds.Shell,
            Name = "test.sh",
            Content = "echo test"
        };

        db.Users.Add(author);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync();

        return (post, artifact);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}