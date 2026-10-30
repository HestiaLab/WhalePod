using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Domain.Interactions;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class PublicContentServiceTests
{
    private static WhalePodDbContext CreateContext() => ServiceTestSupport.CreateContext();

    [DatabaseFact]
    public async Task Feed_hides_non_public_posts_and_supports_sort_filter_and_cursor()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var data = await SeedFeedAsync(db);
        var service = new PublicContentService(db);

        var latest = await service.GetFeedAsync("latest", data.CategorySlug, null, 2, null);
        Assert.False(latest.InvalidCursor);
        Assert.Equal(new[] { data.PublishedNewest, data.PublishedMiddle }, latest.Items.Select(item => item.Id));
        Assert.NotNull(latest.NextCursor);
        Assert.DoesNotContain(latest.Items, item => item.Id == data.PendingPost || item.Id == data.DeletedPost);

        var latestPageTwo = await service.GetFeedAsync("latest", data.CategorySlug, null, 2, latest.NextCursor);
        Assert.False(latestPageTwo.InvalidCursor);
        Assert.Equal(new[] { data.PublishedOldest }, latestPageTwo.Items.Select(item => item.Id));
        Assert.Null(latestPageTwo.NextCursor);

        var active = await service.GetFeedAsync("active", data.CategorySlug, null, 3, null);
        Assert.Equal(
            new[] { data.PublishedOldest, data.PublishedMiddle, data.PublishedNewest },
            active.Items.Select(item => item.Id));

        var unresolved = await service.GetFeedAsync("unresolved", data.CategorySlug, null, 3, null);
        Assert.Equal(
            new[] { data.PublishedNewest, data.PublishedOldest },
            unresolved.Items.Select(item => item.Id));

        var tagged = await service.GetFeedAsync("latest", data.CategorySlug, data.TagASlug, 10, null);
        Assert.Equal(new[] { data.PublishedMiddle, data.PublishedOldest }, tagged.Items.Select(item => item.Id));

        var hiddenTag = await service.GetFeedAsync("latest", data.CategorySlug, data.HiddenTagSlug, 10, null);
        Assert.Empty(hiddenTag.Items);

        var mismatched = await service.GetFeedAsync("active", data.CategorySlug, null, 2, latest.NextCursor);
        Assert.True(mismatched.InvalidCursor);

        var details = Assert.Single(latestPageTwo.Items, item => item.Id == data.PublishedOldest);
        Assert.Equal(10, details.Counts.Views);
        Assert.Equal(3, details.Counts.Copies);
        Assert.Equal(2, details.Counts.Likes);
        Assert.Equal(4, details.Counts.Comments);
        Assert.Equal(1, details.Counts.Artifacts);
        Assert.True(details.Author.Badge.Verified);
        Assert.Equal(2, details.Author.Badge.ActiveDeviceCount);
        Assert.NotNull(details.Author.Badge.LastVerifiedAt);
        Assert.Equal(data.ThumbnailUrl, details.ThumbnailUrl);
        Assert.Equal(new[] { data.TagASlug }, details.Tags.Select(tag => tag.Slug));

        var otherCategory = await service.GetFeedAsync("latest", data.OtherCategorySlug, null, 10, null);
        Assert.Contains(otherCategory.Items, item => item.Id == data.OtherCategoryPost);
        Assert.DoesNotContain(otherCategory.Items, item => item.Id == data.PublishedOldest);
    }

    [DatabaseFact]
    public async Task Post_detail_returns_full_public_shape_and_hides_unapproved()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var data = await SeedFeedAsync(db);
        var service = new PublicContentService(db);

        var detail = Assert.IsType<PostDetailView>(await service.GetPostAsync(data.PublishedOldest));
        Assert.Equal(data.PublishedOldest, detail.Id);
        Assert.Equal(data.Version, detail.Version);
        Assert.Contains("public body", detail.BodyMd);
        Assert.Single(detail.Images);
        Assert.Single(detail.Artifacts);
        Assert.Equal("shell", detail.Artifacts[0].Kind);
        Assert.Equal(new[] { data.TagASlug }, detail.Tags.Select(tag => tag.Slug));
        Assert.Equal(4, detail.Counts.Comments);
        Assert.Equal(2, detail.Counts.Likes);

        Assert.Null(await service.GetPostAsync(data.PendingPost));
        Assert.Null(await service.GetPostAsync(data.DeletedPost));
    }

    [DatabaseFact]
    public async Task Categories_and_tags_only_return_public_entries_with_counts()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var data = await SeedFeedAsync(db);
        var service = new PublicContentService(db);

        var categories = await service.GetCategoriesAsync();
        Assert.Contains(categories, category => category.Slug == data.CategorySlug);
        Assert.DoesNotContain(categories, category => category.Slug == data.DisabledCategorySlug);

        var tags = await service.GetTagsAsync(50, null);
        Assert.False(tags.InvalidCursor);
        Assert.Contains(tags.Items, tag => tag.Slug == data.TagASlug && tag.PostCount == 2);
        Assert.Contains(tags.Items, tag => tag.Slug == data.TagBSlug && tag.PostCount == 1);
        Assert.DoesNotContain(tags.Items, tag => tag.Slug == data.HiddenTagSlug);
    }

    [DatabaseFact]
    public async Task Comments_include_deleted_parent_placeholder_and_support_sort_and_cursor()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var data = await SeedFeedAsync(db);
        var service = new PublicContentService(db);

        var page = Assert.IsType<CommentPage>(
            await service.GetCommentsAsync(data.PublishedOldest, "latest", 2, null));
        Assert.False(page.InvalidCursor);
        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextCursor);

        var deletedParent = Assert.Single(page.Items, item => item.Id == data.DeletedParentComment);
        Assert.True(deletedParent.IsDeleted);
        Assert.Null(deletedParent.BodyMd);
        Assert.Null(deletedParent.BodyHtml);
        Assert.DoesNotContain(page.Items, item => item.Id == data.PendingComment);

        var secondPage = Assert.IsType<CommentPage>(
            await service.GetCommentsAsync(data.PublishedOldest, "latest", 2, page.NextCursor));
        var allComments = page.Items.Concat(secondPage.Items).ToList();
        Assert.DoesNotContain(allComments, item => item.Id == data.PendingComment);

        var top = Assert.IsType<CommentPage>(
            await service.GetCommentsAsync(data.PublishedOldest, "top", 10, null));
        Assert.Equal(5, top.Items[0].LikeCount);
        Assert.Equal(data.DeletedParentComment, top.Items[1].Id);

        var mismatched = Assert.IsType<CommentPage>(
            await service.GetCommentsAsync(data.PublishedOldest, "top", 2, page.NextCursor));
        Assert.True(mismatched.InvalidCursor);

        Assert.Null(await service.GetCommentsAsync(data.PendingPost, "latest", 10, null));
    }

    [DatabaseFact]
    public async Task Tag_detail_and_user_profiles_expose_public_stats()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var data = await SeedFeedAsync(db);
        var service = new PublicContentService(db);

        var tagPage = Assert.IsType<TagDetailView>(
            await service.GetTagDetailAsync(data.TagASlug, 1, null));
        Assert.Equal(2, tagPage.Tag.PostCount);
        Assert.Single(tagPage.Posts.Items);
        Assert.NotNull(tagPage.Posts.NextCursor);

        var tagPageTwo = Assert.IsType<TagDetailView>(
            await service.GetTagDetailAsync(data.TagASlug, 1, tagPage.Posts.NextCursor));
        Assert.Contains(tagPageTwo.Posts.Items, item => item.Id == data.PublishedOldest);
        Assert.Null(await service.GetTagDetailAsync(data.HiddenTagSlug, 10, null));

        var author = Assert.IsType<UserProfileView>(
            await service.GetUserProfileAsync(data.AuthorHandle.ToUpperInvariant()));
        Assert.Equal(4, author.Stats.Posts);
        Assert.Equal(0, author.Stats.Comments);
        Assert.Equal(2, author.Stats.LikesReceived);
        Assert.True(author.Badge.Verified);
        Assert.Equal(2, author.Badge.ActiveDeviceCount);

        var commenter = Assert.IsType<UserProfileView>(
            await service.GetUserProfileAsync(data.CommenterHandle));
        Assert.Equal(5, commenter.Stats.Comments);
        Assert.Equal(1, commenter.Stats.AcceptedAnswers);
        Assert.Equal(1, commenter.Stats.LikesReceived);

        Assert.Null(await service.GetUserProfileAsync(data.BannedHandle));

        var userPosts = Assert.IsType<FeedPage>(
            await service.GetUserPostsAsync(data.AuthorHandle, 2, null));
        Assert.Equal(
            new[] { data.OtherCategoryPost, data.PublishedNewest },
            userPosts.Items.Select(item => item.Id));
        var userPostsPageTwo = Assert.IsType<FeedPage>(
            await service.GetUserPostsAsync(data.AuthorHandle, 2, userPosts.NextCursor));
        Assert.Equal(
            new[] { data.PublishedMiddle, data.PublishedOldest },
            userPostsPageTwo.Items.Select(item => item.Id));
        Assert.Null(await service.GetUserPostsAsync("no-such-user", 10, null));
    }

    private static async Task<SeedData> SeedFeedAsync(WhalePodDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var now = DateTimeOffset.UtcNow;

        var author = NewUser($"author-{suffix}");
        var commenter = NewUser($"commenter-{suffix}");
        var category = new Category
        {
            Slug = $"feed-{suffix}",
            Name = "公开信息流测试板块",
            Status = CategoryStatuses.Active,
            DisplayOrder = 1
        };
        var otherCategory = new Category
        {
            Slug = $"other-{suffix}",
            Name = "其他板块",
            Status = CategoryStatuses.Active,
            DisplayOrder = 2
        };
        var disabledCategory = new Category
        {
            Slug = $"disabled-{suffix}",
            Name = "停用板块",
            Status = CategoryStatuses.Disabled
        };
        var tagA = NewTag($"tag-a-{suffix}");
        var tagB = NewTag($"tag-b-{suffix}");
        var hiddenTag = NewTag($"tag-hidden-{suffix}", TagStatuses.Hidden);

        var oldest = NewPost(author.Id, category.Id, "Public feed oldest post", ModerationStatuses.Approved, now.AddHours(-3));
        oldest.LastActivityAt = now.AddMinutes(-30);
        oldest.ViewCount = 10;
        oldest.CopyCount = 3;
        oldest.Version = 3;
        var middle = NewPost(author.Id, category.Id, "Public feed middle post", ModerationStatuses.Approved, now.AddHours(-2));
        middle.LastActivityAt = now.AddHours(-2);
        var acceptedComment = new Comment
        {
            PostId = middle.Id,
            AuthorId = commenter.Id,
            BodyMd = "accepted answer",
            BodyHtml = "<p>accepted answer</p>",
            ModerationStatus = ModerationStatuses.Approved,
            IsAccepted = true
        };
        middle.AcceptedCommentId = acceptedComment.Id;
        middle.IsSolved = true;
        var newest = NewPost(author.Id, category.Id, "Public feed newest post", ModerationStatuses.Approved, now.AddHours(-1));
        newest.LastActivityAt = now.AddHours(-3);
        var pending = NewPost(author.Id, category.Id, "Pending feed post", ModerationStatuses.Pending, now.AddMinutes(-30));
        var deleted = NewPost(author.Id, category.Id, "Deleted feed post", ModerationStatuses.Approved, now.AddMinutes(-10));
        deleted.DeletedAt = now.AddMinutes(-5);
        var other = NewPost(author.Id, otherCategory.Id, "Other category post", ModerationStatuses.Approved, now.AddMinutes(-20));

        var artifact = new Artifact
        {
            PostId = oldest.Id,
            Kind = ArtifactKinds.Shell,
            Name = "repair.sh",
            Content = "echo repair",
            SortOrder = 0
        };
        var upload = new MediaUpload
        {
            OwnerId = author.Id,
            PostId = oldest.Id,
            Url = $"/media/{suffix}.png",
            Width = 640,
            Height = 480,
            MimeType = "image/png",
            ByteSize = 100,
            Status = MediaUploadStatuses.Attached,
            AttachedAt = now
        };
        var image = new PostImage
        {
            PostId = oldest.Id,
            UploadId = upload.Id,
            Url = upload.Url,
            Width = 640,
            Height = 480,
            AltText = "测试图片",
            MimeType = "image/png",
            ByteSize = 100,
            SortOrder = 0
        };

        var likes = new[]
        {
            new PostLike { PostId = oldest.Id, UserId = commenter.Id },
            new PostLike { PostId = oldest.Id, UserId = author.Id }
        };
        var approvedComment = new Comment
        {
            PostId = oldest.Id,
            AuthorId = commenter.Id,
            BodyMd = "approved comment",
            BodyHtml = "<p>approved comment</p>",
            ModerationStatus = ModerationStatuses.Approved,
            LikeCount = 1,
            CreatedAt = now.AddHours(-3)
        };
        var topComment = new Comment
        {
            PostId = oldest.Id,
            AuthorId = commenter.Id,
            BodyMd = "top comment",
            BodyHtml = "<p>top comment</p>",
            ModerationStatus = ModerationStatuses.Approved,
            LikeCount = 5,
            CreatedAt = now.AddHours(-2)
        };
        var replyComment = new Comment
        {
            PostId = oldest.Id,
            AuthorId = commenter.Id,
            ParentId = approvedComment.Id,
            BodyMd = "reply comment",
            BodyHtml = "<p>reply comment</p>",
            ModerationStatus = ModerationStatuses.Approved,
            CreatedAt = now.AddMinutes(-90)
        };
        var deletedParentComment = new Comment
        {
            PostId = oldest.Id,
            AuthorId = commenter.Id,
            BodyMd = "deleted parent comment",
            BodyHtml = "<p>deleted parent comment</p>",
            ModerationStatus = ModerationStatuses.Approved,
            LikeCount = 2,
            CreatedAt = now.AddHours(-1),
            DeletedAt = now.AddMinutes(-30)
        };
        var childOfDeletedComment = new Comment
        {
            PostId = oldest.Id,
            AuthorId = commenter.Id,
            ParentId = deletedParentComment.Id,
            BodyMd = "child of deleted comment",
            BodyHtml = "<p>child of deleted comment</p>",
            ModerationStatus = ModerationStatuses.Approved,
            CreatedAt = now.AddMinutes(-45)
        };
        var pendingComment = new Comment
        {
            PostId = oldest.Id,
            AuthorId = commenter.Id,
            BodyMd = "pending comment",
            BodyHtml = "<p>pending comment</p>",
            ModerationStatus = ModerationStatuses.Pending,
            CreatedAt = now.AddMinutes(-20)
        };
        var rejectedComment = new Comment
        {
            PostId = oldest.Id,
            AuthorId = commenter.Id,
            BodyMd = "rejected comment",
            BodyHtml = "<p>rejected comment</p>",
            ModerationStatus = ModerationStatuses.Rejected,
            CreatedAt = now.AddMinutes(-15)
        };

        var devices = new[]
        {
            NewDevice(author.Id, now.AddDays(-2), now.AddHours(-1), DeviceStatuses.Active),
            NewDevice(author.Id, now.AddDays(-3), now.AddDays(-1), DeviceStatuses.Active),
            NewDevice(author.Id, now.AddDays(-4), now.AddDays(-2), DeviceStatuses.Revoked)
        };

        var banned = new User
        {
            Handle = $"banned-{suffix}",
            DisplayName = "封禁用户",
            Status = UserStatuses.Banned,
            Role = UserRoles.Member,
            StatusReason = "测试封禁",
            ActivatedAt = now
        };

        db.Users.AddRange(author, commenter, banned);
        db.Categories.AddRange(category, otherCategory, disabledCategory);
        db.Tags.AddRange(tagA, tagB, hiddenTag);
        db.Posts.AddRange(oldest, middle, newest, pending, deleted, other);
        db.Artifacts.Add(artifact);
        db.MediaUploads.Add(upload);
        db.PostImages.Add(image);
        db.PostLikes.AddRange(likes);
        db.Comments.AddRange(
            approvedComment,
            topComment,
            replyComment,
            deletedParentComment,
            childOfDeletedComment,
            pendingComment,
            rejectedComment,
            acceptedComment);
        db.CommentLikes.Add(new CommentLike
        {
            CommentId = approvedComment.Id,
            UserId = author.Id
        });
        db.Devices.AddRange(devices);
        db.PostTags.AddRange(
            new PostTag { PostId = oldest.Id, TagId = tagA.Id },
            new PostTag { PostId = middle.Id, TagId = tagA.Id },
            new PostTag { PostId = middle.Id, TagId = tagB.Id },
            new PostTag { PostId = middle.Id, TagId = hiddenTag.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new SeedData(
            category.Slug,
            otherCategory.Slug,
            disabledCategory.Slug,
            tagA.Slug,
            tagB.Slug,
            hiddenTag.Slug,
            oldest.Id,
            middle.Id,
            newest.Id,
            pending.Id,
            deleted.Id,
            other.Id,
            pendingComment.Id,
            deletedParentComment.Id,
            commenter.Handle,
            banned.Handle,
            author.Handle,
            upload.Url,
            oldest.Version);
    }

    private static User NewUser(string handle)
    {
        return new User
        {
            Handle = handle,
            DisplayName = "公开内容测试用户",
            Status = UserStatuses.Active,
            Role = UserRoles.Member,
            ActivatedAt = DateTimeOffset.UtcNow
        };
    }

    private static Tag NewTag(string slug, string status = TagStatuses.Active)
    {
        return new Tag
        {
            Slug = slug,
            Name = slug,
            Status = status
        };
    }

    private static Post NewPost(Guid authorId, Guid categoryId, string title, string moderationStatus, DateTimeOffset createdAt)
    {
        return new Post
        {
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = title,
            BodyMd = "This is a public body long enough.",
            BodyHtml = "<p>This is a public body long enough.</p>",
            Excerpt = "public excerpt",
            ModerationStatus = moderationStatus,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActivityAt = createdAt
        };
    }

    private static Device NewDevice(
        Guid userId,
        DateTimeOffset createdAt,
        DateTimeOffset lastVerifiedAt,
        string status)
    {
        return new Device
        {
            UserId = userId,
            PublicKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            KernelVersion = "6.8.0-45-generic",
            Platform = Platforms.LinuxX64,
            Status = status,
            CreatedAt = createdAt,
            LastVerifiedAt = lastVerifiedAt,
            RevokedAt = status == DeviceStatuses.Revoked ? lastVerifiedAt : null
        };
    }

    private sealed record SeedData(
        string CategorySlug,
        string OtherCategorySlug,
        string DisabledCategorySlug,
        string TagASlug,
        string TagBSlug,
        string HiddenTagSlug,
        Guid PublishedOldest,
        Guid PublishedMiddle,
        Guid PublishedNewest,
        Guid PendingPost,
        Guid DeletedPost,
        Guid OtherCategoryPost,
        Guid PendingComment,
        Guid DeletedParentComment,
        string CommenterHandle,
        string BannedHandle,
        string AuthorHandle,
        string ThumbnailUrl,
        int Version);
}