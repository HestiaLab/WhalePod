using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class ModerationQueueTests
{
    [DatabaseFact]
    public async Task Queue_prioritizes_escalations_on_the_current_version_only()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("queue-escalate-author");
        var admin = ServiceTestSupport.NewUser("queue-escalate-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(author, admin);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var baseTime = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var normalOld = NewPendingPost(db, author.Id, category.Id, "队列普通旧帖标题", baseTime);
        var escalatedOld = NewPendingPost(db, author.Id, category.Id, "队列升级旧帖标题", baseTime.AddMinutes(1));
        var escalatedNew = NewPendingPost(db, author.Id, category.Id, "队列升级新帖标题", baseTime.AddMinutes(2));
        var normalNew = NewPendingPost(db, author.Id, category.Id, "队列普通新帖标题", baseTime.AddMinutes(3));
        await db.SaveChangesAsync();

        db.ModerationActions.Add(NewEscalation(escalatedOld, admin.Id, baseTime.AddMinutes(4)));
        db.ModerationTasks.Add(NewTask(
            ModerationTargetTypes.Post,
            escalatedNew.Id,
            escalatedNew.Version,
            ModerationTaskStatuses.Escalated,
            baseTime.AddMinutes(4)));
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var beforeEdit = await WalkQueueAsync(services.Moderation, admin.Id, null, 50);
        var positions = PositionOf(beforeEdit);
        Assert.True(positions[Key("post", escalatedOld.Id)] < positions[Key("post", normalOld.Id)]);
        Assert.True(positions[Key("post", escalatedOld.Id)] < positions[Key("post", normalNew.Id)]);
        Assert.True(positions[Key("post", escalatedNew.Id)] < positions[Key("post", normalOld.Id)]);
        Assert.True(positions[Key("post", escalatedNew.Id)] < positions[Key("post", normalNew.Id)]);
        Assert.True(
            positions[Key("post", escalatedOld.Id)] < positions[Key("post", escalatedNew.Id)]);

        var updated = await services.Posts.UpdateAsync(
            author.Id,
            escalatedOld.Id,
            1,
            new PostUpdateCommand(
                "编辑后的升级旧帖标题",
                null,
                "编辑后的正文内容仍然足够长，用于重新等待审核。",
                null,
                null,
                null,
                null,
                null,
                null),
            ServiceTestSupport.NewAudit(author.Id));
        Assert.Equal(2, updated.Version);

        var afterEdit = await WalkQueueAsync(services.Moderation, admin.Id, null, 50);
        var editedPositions = PositionOf(afterEdit);
        var editedKey = Key("post", escalatedOld.Id);
        Assert.True(editedPositions[Key("post", escalatedNew.Id)] < editedPositions[editedKey]);
        Assert.True(editedPositions[Key("post", normalOld.Id)] < editedPositions[editedKey]);
        Assert.True(editedPositions[Key("post", normalNew.Id)] < editedPositions[editedKey]);

        var item = await ServiceTestSupport.FindQueueItemAsync(
            services.Moderation,
            admin.Id,
            "post",
            escalatedOld.Id);
        Assert.NotNull(item);
        Assert.Equal(2, item.ContentVersion);
    }

    [DatabaseFact]
    public async Task Queue_paginates_prioritized_mixed_targets_without_duplicates_or_gaps()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        // The shared test database accumulates pending fixtures across runs; keep this pagination walk scoped.
        var cleanupTime = DateTimeOffset.UtcNow;
        await db.Posts
            .Where(post => post.ModerationStatus == ModerationStatuses.Pending && post.DeletedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(post => post.DeletedAt, cleanupTime));
        await db.Comments
            .Where(comment => comment.ModerationStatus == ModerationStatuses.Pending && comment.DeletedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(comment => comment.DeletedAt, cleanupTime));

        var author = ServiceTestSupport.NewUser("queue-page-author");
        var admin = ServiceTestSupport.NewUser("queue-page-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(author, admin);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var parent = ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            ModerationStatuses.Approved,
            "队列分页父帖标题");
        db.Posts.Add(parent);
        await db.SaveChangesAsync();

        var baseTime = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.Zero);
        var posts = new List<Post>();
        for (var index = 0; index < 5; index++)
        {
            posts.Add(NewPendingPost(
                db,
                author.Id,
                category.Id,
                $"队列分页帖子标题 {index}",
                baseTime.AddMinutes(index)));
        }

        var comments = new List<Comment>();
        for (var index = 0; index < 5; index++)
        {
            var comment = ServiceTestSupport.NewComment(
                parent.Id,
                author.Id,
                ModerationStatuses.Pending,
                body: $"队列分页评论 {index}");
            comment.CreatedAt = baseTime.AddMinutes(index + 10);
            comment.UpdatedAt = baseTime.AddMinutes(index + 10);
            comments.Add(comment);
            db.Comments.Add(comment);
        }

        await db.SaveChangesAsync();

        db.ModerationActions.Add(NewEscalation(posts[0], admin.Id, baseTime.AddMinutes(20)));
        db.ModerationTasks.Add(NewTask(
            ModerationTargetTypes.Comment,
            comments[1].Id,
            comments[1].Version,
            ModerationTaskStatuses.Escalated,
            baseTime.AddMinutes(20)));
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var allItems = await WalkQueueAsync(services.Moderation, admin.Id, null, 2);
        Assert.Equal(allItems.Count, allItems.Distinct().Count());
        var allKeys = new HashSet<(string, Guid)>(allItems);
        foreach (var post in posts)
        {
            Assert.Contains(Key("post", post.Id), allKeys);
        }

        foreach (var comment in comments)
        {
            Assert.Contains(Key("comment", comment.Id), allKeys);
        }

        var positions = PositionOf(allItems);
        Assert.Equal(1, allItems.Count(item => item == Key("post", posts[0].Id)));
        Assert.Equal(1, allItems.Count(item => item == Key("comment", comments[1].Id)));
        var escalatedPositions = new[]
        {
            positions[Key("post", posts[0].Id)],
            positions[Key("comment", comments[1].Id)]
        };
        var normalPositions = posts
            .Skip(1)
            .Select(post => positions[Key("post", post.Id)])
            .Concat(comments
                .Where((_, index) => index != 1)
                .Select(comment => positions[Key("comment", comment.Id)]))
            .ToList();
        Assert.All(normalPositions, normal => Assert.All(
            escalatedPositions,
            escalated => Assert.True(escalated < normal)));

        var postItems = await WalkQueueAsync(services.Moderation, admin.Id, "post", 2);
        Assert.Equal(postItems.Count, postItems.Distinct().Count());
        Assert.All(postItems, item => Assert.Equal("post", item.Item1));
        foreach (var post in posts)
        {
            Assert.Contains(Key("post", post.Id), postItems);
        }

        var commentItems = await WalkQueueAsync(services.Moderation, admin.Id, "comment", 2);
        Assert.Equal(commentItems.Count, commentItems.Distinct().Count());
        Assert.All(commentItems, item => Assert.Equal("comment", item.Item1));
        foreach (var comment in comments)
        {
            Assert.Contains(Key("comment", comment.Id), commentItems);
        }

        var crossScope = await services.Moderation.GetQueueAsync(
            admin.Id,
            "comment",
            2,
            EncodeForeignCursor());
        Assert.True(crossScope.InvalidCursor);

        var member = ServiceTestSupport.NewUser("queue-page-member");
        db.Users.Add(member);
        await db.SaveChangesAsync();
        var forbidden = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Moderation.GetQueueAsync(member.Id, null, 20, null));
        Assert.Equal(ApiErrorCodes.Forbidden, forbidden.Code);
    }

    [DatabaseFact]
    public async Task Queue_maps_current_version_suggestions_and_risk_labels_without_faking_missing_data()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var author = ServiceTestSupport.NewUser("queue-ai-author");
        var admin = ServiceTestSupport.NewUser("queue-ai-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(author, admin);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var account = new ServiceAccount
        {
            Name = $"queue-ai-svc-{ServiceTestSupport.NewSuffix()}",
            OwnerId = admin.Id,
            Scopes = ["moderation:suggest"]
        };
        var agent = new AgentInstance
        {
            ServiceAccountId = account.Id,
            InstanceName = $"queue-ai-agent-{ServiceTestSupport.NewSuffix()}",
            HostFingerprint = new string('c', 64),
            AgentVersion = "0.1.0",
            ModelName = "deepseek-flash",
            ModelVersion = "2026-10",
            ModelQuantization = "q4_k_m"
        };
        db.ServiceAccounts.Add(account);
        db.AgentInstances.Add(agent);
        await db.SaveChangesAsync();

        var withSuggestion = NewPendingPost(
            db,
            author.Id,
            category.Id,
            "队列 AI 建议帖子标题",
            new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));
        var withoutData = NewPendingPost(
            db,
            author.Id,
            category.Id,
            "队列无 AI 数据帖子标题",
            new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var currentTask = NewTask(
            ModerationTargetTypes.Post,
            withSuggestion.Id,
            withSuggestion.Version,
            ModerationTaskStatuses.Suggested,
            new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        db.ModerationTasks.Add(currentTask);
        await db.SaveChangesAsync();
        var currentSuggestion = NewSuggestion(
            currentTask.Id,
            account.Id,
            agent.Id,
            "escalate",
            new DateTimeOffset(2026, 3, 1, 10, 5, 0, TimeSpan.Zero));
        db.ModerationSuggestions.Add(currentSuggestion);
        db.ModerationChecks.Add(new ModerationCheck
        {
            TargetType = ModerationTargetTypes.Post,
            TargetId = withSuggestion.Id,
            ContentVersion = withSuggestion.Version,
            Provider = "test-provider",
            RiskLabels = JsonDocument.Parse("[\"spam\",\"contact_info\"]"),
            Result = ModerationCheckResults.Review,
            CreatedAt = new DateTimeOffset(2026, 3, 1, 10, 6, 0, TimeSpan.Zero)
        });
        await db.SaveChangesAsync();

        var staleVersion = NewPendingPost(
            db,
            author.Id,
            category.Id,
            "队列旧版本建议帖子标题",
            new DateTimeOffset(2026, 3, 1, 11, 0, 0, TimeSpan.Zero));
        staleVersion.Version = 3;
        await db.SaveChangesAsync();
        var staleTask = NewTask(
            ModerationTargetTypes.Post,
            staleVersion.Id,
            2,
            ModerationTaskStatuses.HumanDecided,
            new DateTimeOffset(2026, 3, 1, 11, 5, 0, TimeSpan.Zero));
        db.ModerationTasks.Add(staleTask);
        await db.SaveChangesAsync();
        db.ModerationSuggestions.Add(NewSuggestion(
            staleTask.Id,
            account.Id,
            agent.Id,
            "approve",
            new DateTimeOffset(2026, 3, 1, 11, 6, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var item = await ServiceTestSupport.FindQueueItemAsync(
            services.Moderation,
            admin.Id,
            "post",
            withSuggestion.Id);
        Assert.NotNull(item);
        Assert.NotNull(item.AiSuggestion);
        Assert.Equal(currentSuggestion.Id, item.AiSuggestion.Id);
        Assert.Equal("escalate", item.AiSuggestion.Suggestion);
        Assert.Equal("spam_ad", item.AiSuggestion.PolicyCode);
        Assert.Equal(0.87m, item.AiSuggestion.Confidence);
        Assert.Equal("建议人工优先复核。", item.AiSuggestion.Reason);
        Assert.Equal("deepseek-flash", item.AiSuggestion.ModelName);
        Assert.Equal("2026-10", item.AiSuggestion.ModelVersion);
        Assert.Equal("q4_k_m", item.AiSuggestion.ModelQuantization);
        Assert.Equal(new[] { "spam", "contact_info" }, item.RiskHints);

        var emptyItem = await ServiceTestSupport.FindQueueItemAsync(
            services.Moderation,
            admin.Id,
            "post",
            withoutData.Id);
        Assert.NotNull(emptyItem);
        Assert.Null(emptyItem.AiSuggestion);
        Assert.Empty(emptyItem.RiskHints);

        var staleItem = await ServiceTestSupport.FindQueueItemAsync(
            services.Moderation,
            admin.Id,
            "post",
            staleVersion.Id);
        Assert.NotNull(staleItem);
        Assert.Null(staleItem.AiSuggestion);
        Assert.Empty(staleItem.RiskHints);
    }

    private static Post NewPendingPost(
        WhalePodDbContext db,
        Guid authorId,
        Guid categoryId,
        string title,
        DateTimeOffset submittedAt)
    {
        var post = ServiceTestSupport.NewPost(
            authorId,
            categoryId,
            ModerationStatuses.Pending,
            title);
        post.CreatedAt = submittedAt;
        post.UpdatedAt = submittedAt;
        post.LastActivityAt = submittedAt;
        db.Posts.Add(post);
        return post;
    }

    private static ModerationAction NewEscalation(Post post, Guid adminId, DateTimeOffset createdAt)
    {
        return new ModerationAction
        {
            TargetType = ModerationTargetTypes.Post,
            TargetId = post.Id,
            ActorType = ModerationActorTypes.User,
            ActorId = adminId,
            Action = ModerationActions.Escalate,
            FromStatus = ModerationStatuses.Pending,
            ToStatus = ModerationStatuses.Pending,
            Reason = "高风险，优先人工复核。",
            ContentVersion = post.Version,
            CreatedAt = createdAt
        };
    }

    private static ModerationTask NewTask(
        string targetType,
        Guid targetId,
        int contentVersion,
        string status,
        DateTimeOffset createdAt)
    {
        return new ModerationTask
        {
            TargetType = targetType,
            TargetId = targetId,
            ContentVersion = contentVersion,
            ContentHash = new string('a', 64),
            Status = status,
            CreatedAt = createdAt
        };
    }

    private static ModerationSuggestion NewSuggestion(
        Guid taskId,
        Guid serviceAccountId,
        Guid agentInstanceId,
        string suggestion,
        DateTimeOffset createdAt)
    {
        return new ModerationSuggestion
        {
            TaskId = taskId,
            ServiceAccountId = serviceAccountId,
            AgentInstanceId = agentInstanceId,
            Suggestion = suggestion,
            PolicyCode = "spam_ad",
            Reason = "建议人工优先复核。",
            Confidence = 0.87m,
            ModelName = "deepseek-flash",
            ModelVersion = "2026-10",
            ModelQuantization = "q4_k_m",
            PromptVersion = "p1",
            PolicyVersion = "v1",
            IdempotencyKey = $"queue-ai-{ServiceTestSupport.NewSuffix()}",
            CreatedAt = createdAt
        };
    }

    private static async Task<List<(string, Guid)>> WalkQueueAsync(
        ContentModerationService moderation,
        Guid adminId,
        string? targetType,
        int limit)
    {
        var items = new List<(string, Guid)>();
        string? cursor = null;
        for (var pageIndex = 0; pageIndex < 500; pageIndex++)
        {
            var page = await moderation.GetQueueAsync(adminId, targetType, limit, cursor);
            Assert.False(page.InvalidCursor);
            items.AddRange(page.Items.Select(item => (item.TargetType, item.TargetId)));
            if (page.NextCursor is null)
            {
                return items;
            }

            cursor = page.NextCursor;
        }

        throw new InvalidOperationException("Queue pagination did not terminate.");
    }

    private static Dictionary<(string, Guid), int> PositionOf(List<(string, Guid)> items)
    {
        var positions = new Dictionary<(string, Guid), int>();
        for (var index = 0; index < items.Count; index++)
        {
            positions[items[index]] = index;
        }

        return positions;
    }

    private static (string, Guid) Key(string targetType, Guid targetId)
    {
        return (targetType, targetId);
    }

    private static string EncodeForeignCursor()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            s = "moderation-queue:0000000000000000",
            t = DateTimeOffset.UtcNow.UtcDateTime.Ticks,
            i = Guid.NewGuid(),
            p = false
        });
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}