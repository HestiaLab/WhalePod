using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class TagAdminServiceTests
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
    public async Task Admin_can_rename_tag_and_audit_old_and_new_name()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var admin = NewUser("tag-admin", UserRoles.Admin);
        var tag = NewTag("alpha");
        db.Users.Add(admin);
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        var originalName = tag.Name;
        var newName = $"新标签名-{Guid.NewGuid().ToString("N")[..8]}";
        var service = CreateService(db);
        var outcome = await service.RenameAsync(tag.Id, $"  {newName}  ", NewAudit(admin.Id));

        Assert.Equal(TagMutationResult.Success, outcome);

        db.ChangeTracker.Clear();
        var renamed = await db.Tags.SingleAsync(candidate => candidate.Id == tag.Id);
        Assert.Equal(newName, renamed.Name);
        Assert.Equal(tag.Slug, renamed.Slug);

        var audit = await db.AuditLogs.SingleAsync(log => log.TargetId == tag.Id && log.Action == "tag.renamed");
        Assert.Equal(admin.Id, audit.ActorId);
        Assert.Equal(originalName, audit.MetadataJson.RootElement.GetProperty("oldName").GetString());
        Assert.Equal(newName, audit.MetadataJson.RootElement.GetProperty("newName").GetString());
    }

    [DatabaseFact]
    public async Task Rename_rejects_case_insensitive_duplicate_name()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var admin = NewUser("tag-dupe", UserRoles.Admin);
        var first = NewTag($"alpha-{suffix}");
        var second = NewTag($"beta-{suffix}");
        db.Users.Add(admin);
        db.Tags.AddRange(first, second);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.RenameAsync(second.Id, first.Name.ToUpperInvariant(), NewAudit(admin.Id));

        Assert.Equal(TagMutationResult.DuplicateName, outcome);

        db.ChangeTracker.Clear();
        Assert.Equal(second.Name, (await db.Tags.SingleAsync(candidate => candidate.Id == second.Id)).Name);
        Assert.False(await db.AuditLogs.AnyAsync(log => log.TargetId == second.Id && log.Action == "tag.renamed"));
    }

    [DatabaseFact]
    public async Task Non_admin_cannot_rename_tag()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var member = NewUser("tag-member", UserRoles.Member);
        var tag = NewTag("member");
        db.Users.Add(member);
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.RenameAsync(tag.Id, "不应生效", NewAudit(member.Id));

        Assert.Equal(TagMutationResult.Forbidden, outcome);

        db.ChangeTracker.Clear();
        Assert.Equal(tag.Name, (await db.Tags.SingleAsync(candidate => candidate.Id == tag.Id)).Name);
    }

    [DatabaseFact]
    public async Task Hide_and_restore_update_status_and_audit_public_post_count()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var admin = NewUser("tag-hide", UserRoles.Admin);
        var category = new Category
        {
            Slug = $"tag-cat-{suffix}",
            Name = "标签测试板块",
            Status = CategoryStatuses.Active
        };
        var tag = NewTag("hidden");
        var approvedPost = NewPost(admin.Id, category.Id, ModerationStatuses.Approved);
        var pendingPost = NewPost(admin.Id, category.Id, ModerationStatuses.Pending);

        db.Users.Add(admin);
        db.Categories.Add(category);
        db.Tags.Add(tag);
        db.Posts.AddRange(approvedPost, pendingPost);
        db.PostTags.AddRange(
            new PostTag { PostId = approvedPost.Id, TagId = tag.Id },
            new PostTag { PostId = pendingPost.Id, TagId = tag.Id });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        Assert.Equal(TagMutationResult.Success, await service.SetVisibilityAsync(tag.Id, true, NewAudit(admin.Id)));

        db.ChangeTracker.Clear();
        Assert.Equal(TagStatuses.Hidden, (await db.Tags.SingleAsync(candidate => candidate.Id == tag.Id)).Status);

        var hideAudit = await db.AuditLogs.SingleAsync(log => log.TargetId == tag.Id && log.Action == "tag.hidden");
        Assert.Equal(1, hideAudit.MetadataJson.RootElement.GetProperty("publicPostCount").GetInt32());

        var listed = await CreateService(db).ListAsync();
        Assert.Equal(1, listed.Single(item => item.Id == tag.Id).PublicPostCount);

        Assert.Equal(TagMutationResult.Success, await CreateService(db).SetVisibilityAsync(tag.Id, false, NewAudit(admin.Id)));

        db.ChangeTracker.Clear();
        Assert.Equal(TagStatuses.Active, (await db.Tags.SingleAsync(candidate => candidate.Id == tag.Id)).Status);
        Assert.True(await db.AuditLogs.AnyAsync(log => log.TargetId == tag.Id && log.Action == "tag.restored"));
    }

    [DatabaseFact]
    public async Task Bootstrap_promotes_configured_handle_only_once()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var user = NewUser("bootstrap", UserRoles.Member);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new AdminBootstrapService(
            db,
            Options.Create(new AdminBootstrapOptions { BootstrapAdminHandles = [user.Handle] }),
            TimeProvider.System,
            NullLogger<AdminBootstrapService>.Instance);

        Assert.True(await service.EnsureAdminAsync(user, NewAudit(user.Id)));

        db.ChangeTracker.Clear();
        var promoted = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        Assert.Equal(UserRoles.Admin, promoted.Role);
        Assert.Equal(1, await db.AuditLogs.CountAsync(log => log.TargetId == user.Id && log.Action == "admin.bootstrap_granted"));

        Assert.False(await service.EnsureAdminAsync(promoted, NewAudit(user.Id)));
        Assert.Equal(1, await db.AuditLogs.CountAsync(log => log.TargetId == user.Id && log.Action == "admin.bootstrap_granted"));
    }

    private static TagAdminService CreateService(WhalePodDbContext db)
    {
        return new TagAdminService(db, new AdminAccessService(db), TimeProvider.System);
    }

    private static AuditContext NewAudit(Guid actorId)
    {
        return new AuditContext(actorId, "test-request", new byte[32]);
    }

    private static User NewUser(string prefix, string role)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new User
        {
            Handle = $"{prefix}-{suffix}",
            DisplayName = "测试用户",
            Status = UserStatuses.Active,
            Role = role,
            ActivatedAt = DateTimeOffset.UtcNow
        };
    }

    private static Tag NewTag(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new Tag
        {
            Slug = $"tag-{prefix}-{suffix}",
            Name = $"标签-{prefix}-{suffix}",
            Status = TagStatuses.Active
        };
    }

    private static Post NewPost(Guid authorId, Guid categoryId, string moderationStatus)
    {
        return new Post
        {
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "标签管理测试帖子",
            BodyMd = "这是一个足够长的标签管理测试正文内容。",
            BodyHtml = "<p>这是一个足够长的标签管理测试正文内容。</p>",
            Excerpt = "标签管理测试",
            ModerationStatus = moderationStatus
        };
    }
}