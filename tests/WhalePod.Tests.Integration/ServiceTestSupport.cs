using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Tests.Integration;

internal static class ServiceTestSupport
{
    public static string ConnectionString => TestDatabaseGuard.FromEnvironment();

    public static WhalePodDbContext CreateContext()
    {
        var connectionString = ConnectionString;
        TestDatabaseGuard.EnsureServerMatches(connectionString);
        var options = new DbContextOptionsBuilder<WhalePodDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new WhalePodDbContext(options);
    }

    public static string NewSuffix()
    {
        return Guid.NewGuid().ToString("N")[..10];
    }

    public static User NewUser(
        string prefix,
        string role = UserRoles.Member,
        string status = UserStatuses.Active,
        bool activated = true,
        DateTimeOffset? statusExpiresAt = null,
        string? statusReason = null,
        short trustLevel = 0)
    {
        var suffix = NewSuffix();
        if (prefix.Length > 21)
        {
            prefix = prefix[..21];
        }

        return new User
        {
            Handle = $"{prefix}-{suffix}",
            DisplayName = $"{prefix} 测试用户",
            Role = role,
            Status = status,
            ActivatedAt = activated ? DateTimeOffset.UtcNow.AddDays(-1) : null,
            StatusExpiresAt = statusExpiresAt,
            StatusReason = statusReason,
            TrustLevel = trustLevel
        };
    }

    public static Category NewCategory(string name = "测试板块", string status = CategoryStatuses.Active)
    {
        return new Category
        {
            Slug = $"cat-{NewSuffix()}",
            Name = name,
            Status = status,
            DisplayOrder = 1
        };
    }

    public static Tag NewTag(string status = TagStatuses.Active)
    {
        var suffix = NewSuffix();
        return new Tag
        {
            Slug = $"tag-{suffix}",
            Name = $"标签{suffix}",
            Status = status
        };
    }

    public static Post NewPost(
        Guid authorId,
        Guid categoryId,
        string status = ModerationStatuses.Approved,
        string title = "测试帖子标题足够长",
        string body = "这是一个足够长的测试正文，用于公开内容读取。")
    {
        return new Post
        {
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = title,
            BodyMd = body,
            BodyHtml = $"<p>{body}</p>",
            Excerpt = body,
            ModerationStatus = status
        };
    }

    public static Comment NewComment(
        Guid postId,
        Guid authorId,
        string status = ModerationStatuses.Approved,
        Guid? parentId = null,
        string body = "测试评论内容")
    {
        return new Comment
        {
            PostId = postId,
            AuthorId = authorId,
            ParentId = parentId,
            BodyMd = body,
            BodyHtml = $"<p>{body}</p>",
            ModerationStatus = status
        };
    }

    public static AuditContext NewAudit(Guid actorId)
    {
        return new AuditContext(actorId, $"test-{NewSuffix()}", new byte[32]);
    }

    public static async Task<ModerationQueueItemView?> FindQueueItemAsync(
        ContentModerationService moderation,
        Guid adminId,
        string targetType,
        Guid targetId)
    {
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await moderation.GetQueueAsync(adminId, targetType, 50, cursor);
            if (page.InvalidCursor)
            {
                return null;
            }

            var item = page.Items.FirstOrDefault(candidate => candidate.TargetId == targetId);
            if (item is not null)
            {
                return item;
            }

            if (page.NextCursor is null)
            {
                return null;
            }

            cursor = page.NextCursor;
        }

        return null;
    }

    public static ImageUploadService CreateImageUploadService(
        WhalePodDbContext db,
        IMediaStorage storage)
    {
        var timeProvider = TimeProvider.System;
        return new ImageUploadService(
            db,
            new ActorGuard(db, timeProvider),
            storage,
            timeProvider,
            NullLogger<ImageUploadService>.Instance);
    }

    public static MediaCleanupService CreateMediaCleanupService(
        WhalePodDbContext db,
        IMediaStorage storage)
    {
        return new MediaCleanupService(
            db,
            storage,
            TimeProvider.System,
            NullLogger<MediaCleanupService>.Instance);
    }

    public static TestServices CreateServices(WhalePodDbContext db)
    {
        var timeProvider = TimeProvider.System;
        var guard = new ActorGuard(db, timeProvider);
        var content = new PublicContentService(db);
        var emailQueue = new WhalePod.Infrastructure.Email.EmailNotificationQueue(db);
        var posts = new PostWriteService(db, guard, content, timeProvider);
        var comments = new CommentWriteService(db, guard, content, emailQueue, timeProvider);
        var moderation = new ContentModerationService(db, guard, emailQueue, timeProvider);
        var adminUsers = new AdminUserService(db, guard, emailQueue, timeProvider);
        var reports = new ReportService(db, guard, moderation, adminUsers, emailQueue, timeProvider);
        var authorContent = new AuthorContentService(db, guard);
        var likes = new LikeService(db, guard, timeProvider);
        var acceptance = new AcceptanceService(db, guard, emailQueue, timeProvider);
        var notifications = new NotificationService(db, guard, timeProvider);
        return new TestServices(
            guard,
            content,
            posts,
            comments,
            moderation,
            adminUsers,
            reports,
            authorContent,
            likes,
            acceptance,
            notifications);
    }
}

internal sealed record TestServices(
    ActorGuard ActorGuard,
    PublicContentService Content,
    PostWriteService Posts,
    CommentWriteService Comments,
    ContentModerationService Moderation,
    AdminUserService AdminUsers,
    ReportService Reports,
    AuthorContentService AuthorContent,
    LikeService Likes,
    AcceptanceService Acceptance,
    NotificationService Notifications);

internal sealed class TestMediaStorage : IDisposable
{
    public TestMediaStorage()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            $"whalepod-test-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
        Storage = new LocalMediaStorage(Options.Create(new MediaStorageOptions
        {
            Provider = MediaStorageProviders.Local,
            PublicBaseUrl = "/media",
            LocalRootPath = RootPath
        }));
    }

    public string RootPath { get; }

    public IMediaStorage Storage { get; }

    public string ResolvePath(string publicUrl)
    {
        Assert.StartsWith("/media/", publicUrl, StringComparison.Ordinal);
        return Path.Combine(
            RootPath,
            publicUrl["/media/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}