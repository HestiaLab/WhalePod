using Microsoft.EntityFrameworkCore;

using Npgsql;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Tests.Integration;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        var connectionString = Environment.GetEnvironmentVariable("WHALEPOD_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Skip = "Set WHALEPOD_TEST_DB_CONNECTION to a disposable database named whalepod_test.";
        }
        else if (!connectionString.Contains("whalepod_test", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Refusing to run database tests against a database not named whalepod_test.";
        }
    }
}

public sealed class PersistenceDatabaseTests
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
    public async Task Migration_creates_all_tables_extensions_and_raw_objects()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'")
            .ToListAsync();
        Assert.Equal(33, tables.Count);
        Assert.Contains("user", tables);
        Assert.Contains("post", tables);
        Assert.Contains("comment", tables);
        Assert.Contains("media_upload", tables);
        Assert.Contains("copy_dedupe", tables);
        Assert.Contains("moderation_suggestion", tables);

        var extensions = await db.Database
            .SqlQueryRaw<string>("SELECT extname AS \"Value\" FROM pg_extension WHERE extname IN ('citext','pg_trgm')")
            .ToListAsync();
        Assert.Contains("citext", extensions);
        Assert.Contains("pg_trgm", extensions);

        var constraints = await db.Database
            .SqlQueryRaw<string>("SELECT conname AS \"Value\" FROM pg_constraint WHERE conname IN ('fk_post_accepted_comment_same_post','fk_comment_parent_same_post')")
            .ToListAsync();
        Assert.Contains("fk_post_accepted_comment_same_post", constraints);
        Assert.Contains("fk_comment_parent_same_post", constraints);

        var indexes = await db.Database
            .SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE indexname IN ('ix_post_feed_latest','ix_post_feed_unresolved','ix_notification_user_time','ix_post_title_trgm')")
            .ToListAsync();
        Assert.Equal(4, indexes.Count);

        var trigger = await db.Database
            .SqlQueryRaw<string>("SELECT tgname AS \"Value\" FROM pg_trigger WHERE tgname = 'trg_audit_log_append_only'")
            .SingleAsync();
        Assert.Equal("trg_audit_log_append_only", trigger);
    }

    [DatabaseFact]
    public async Task User_check_constraint_and_handle_unique_are_enforced()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var invalid = NewUser("invalid-status");
        invalid.Status = "not-a-status";
        db.Users.Add(invalid);

        var checkException = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(checkException.InnerException).SqlState);

        db.ChangeTracker.Clear();

        var valid = NewUser("unique");
        db.Users.Add(valid);
        await db.SaveChangesAsync();

        var duplicate = NewUser("unique");
        duplicate.Handle = valid.Handle;
        db.Users.Add(duplicate);

        var uniqueException = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(uniqueException.InnerException).SqlState);
    }

    [DatabaseFact]
    public async Task Accepted_comment_must_belong_to_the_same_post()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var author = NewUser("accepted");
        var category = new Category
        {
            Slug = $"cat-{suffix}",
            Name = "测试板块",
            Status = CategoryStatuses.Active
        };

        var firstPost = NewPost(author.Id, category.Id, "第一帖标题需要更长的内容");
        var secondPost = NewPost(author.Id, category.Id, "第二帖标题需要更长的内容");
        var foreignComment = new Comment
        {
            PostId = secondPost.Id,
            AuthorId = author.Id,
            BodyMd = "这是第二帖的评论",
            BodyHtml = "<p>这是第二帖的评论</p>",
            ModerationStatus = ModerationStatuses.Approved
        };

        db.Users.Add(author);
        db.Categories.Add(category);
        db.Posts.AddRange(firstPost, secondPost);
        db.Comments.Add(foreignComment);
        await db.SaveChangesAsync();

        firstPost.AcceptedCommentId = foreignComment.Id;
        firstPost.IsSolved = true;
        firstPost.Version++;

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("23503", postgres.SqlState);
        Assert.Equal("fk_post_accepted_comment_same_post", postgres.ConstraintName);
    }

    [DatabaseFact]
    public async Task Audit_log_is_append_only()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var entry = new AuditLog
        {
            ActorType = AuditActorTypes.System,
            Action = "test.append_only",
            MetadataJson = System.Text.Json.JsonDocument.Parse("{}")
        };

        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE audit_log SET action = 'tampered' WHERE id = {0}", entry.Id));

        Assert.Equal("P0001", exception.SqlState);
    }

    [DatabaseFact]
    public async Task Service_account_ip_allowlist_roundtrips_as_inet_array()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = NewUser("inet-owner");
        var account = new ServiceAccount
        {
            Name = $"inet-{suffix}",
            OwnerId = owner.Id,
            Scopes = ["agent:heartbeat"],
            IpAllowlist = [System.Net.IPAddress.Parse("127.0.0.1")]
        };

        db.Users.Add(owner);
        db.ServiceAccounts.Add(account);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var reloaded = await db.ServiceAccounts.SingleAsync(candidate => candidate.Id == account.Id);
        Assert.Equal([System.Net.IPAddress.Parse("127.0.0.1")], reloaded.IpAllowlist);
    }

    private static User NewUser(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new User
        {
            Handle = $"{prefix}-{suffix}",
            DisplayName = "测试用户",
            Status = UserStatuses.Active,
            Role = UserRoles.Member
        };
    }

    private static Post NewPost(Guid authorId, Guid categoryId, string title)
    {
        return new Post
        {
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = title,
            BodyMd = "这是一个足够长的测试正文内容。",
            BodyHtml = "<p>这是一个足够长的测试正文内容。</p>",
            Excerpt = "测试摘要",
            ModerationStatus = ModerationStatuses.Approved
        };
    }
}