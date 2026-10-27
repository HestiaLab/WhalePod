using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Domain.Interactions;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class NotificationServiceTests
{
    [DatabaseFact]
    public async Task List_is_owner_scoped_paginated_and_sanitizes_payloads()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var owner = ServiceTestSupport.NewUser("notify-list-owner");
        var other = ServiceTestSupport.NewUser("notify-list-other");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(owner.Id, category.Id);
        var otherPost = ServiceTestSupport.NewPost(other.Id, category.Id);
        var commentId = Guid.CreateVersion7();
        db.Users.AddRange(owner, other);
        db.Categories.Add(category);
        db.Posts.AddRange(post, otherPost);
        await db.SaveChangesAsync();

        var baseTime = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
        var reply = NewNotification(
            owner.Id,
            NotificationTypes.Reply,
            new
            {
                summary = "有人回复了你的评论。",
                targetType = "comment",
                targetId = commentId,
                targetUrl = $"/posts/{post.Id}?comment={commentId}",
                postId = post.Id
            },
            baseTime.AddMinutes(-1));
        var unsafePayload = NewNotification(
            owner.Id,
            NotificationTypes.PostApproved,
            new
            {
                targetType = "post",
                targetId = post.Id,
                targetUrl = "https://evil.example/steal",
                email = "secret@example.com",
                reason = "不应暴露的内部理由"
            },
            baseTime.AddMinutes(-2));
        var readNotification = NewNotification(
            owner.Id,
            NotificationTypes.AccountStatusChanged,
            new { status = UserStatuses.Muted, reason = "测试禁言" },
            baseTime.AddMinutes(-3));
        readNotification.ReadAt = baseTime;
        var otherNotification = NewNotification(
            other.Id,
            NotificationTypes.Reply,
            new
            {
                summary = "其他人的回复。",
                targetType = "comment",
                targetId = Guid.CreateVersion7(),
                targetUrl = $"/posts/{otherPost.Id}"
            },
            baseTime.AddMinutes(-1));
        db.Notifications.AddRange(reply, unsafePayload, readNotification, otherNotification);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var firstPage = await services.Notifications.ListAsync(owner.Id, "all", 2, null);
        Assert.False(firstPage.InvalidCursor);
        Assert.Equal(2, firstPage.UnreadCount);
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal([reply.Id, unsafePayload.Id], firstPage.Items.Select(item => item.Id));

        var replyView = firstPage.Items[0];
        Assert.Equal(NotificationTypes.Reply, replyView.Type);
        Assert.Equal("有人回复了你的评论。", replyView.Payload.Summary);
        Assert.Equal("comment", replyView.Payload.TargetType);
        Assert.Equal(commentId, replyView.Payload.TargetId);
        Assert.Equal($"/posts/{post.Id}?comment={commentId}", replyView.Payload.TargetUrl);

        var unsafeView = firstPage.Items[1];
        Assert.Equal("你的帖子已通过审核。", unsafeView.Payload.Summary);
        Assert.Equal("post", unsafeView.Payload.TargetType);
        Assert.Equal(post.Id, unsafeView.Payload.TargetId);
        Assert.Equal($"/posts/{post.Id}", unsafeView.Payload.TargetUrl);
        var serializedPayload = JsonSerializer.Serialize(
            unsafeView,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("secret@example.com", serializedPayload);
        Assert.DoesNotContain("不应暴露的内部理由", serializedPayload);
        Assert.DoesNotContain("evil.example", serializedPayload);

        var secondPage = await services.Notifications.ListAsync(owner.Id, "all", 2, firstPage.NextCursor);
        Assert.False(secondPage.InvalidCursor);
        Assert.Null(secondPage.NextCursor);
        Assert.Equal(readNotification.Id, Assert.Single(secondPage.Items).Id);
        Assert.NotNull(secondPage.Items[0].ReadAt);

        var unreadPage = await services.Notifications.ListAsync(owner.Id, "unread", 50, null);
        Assert.Equal(2, unreadPage.UnreadCount);
        Assert.Equal([reply.Id, unsafePayload.Id], unreadPage.Items.Select(item => item.Id));

        var crossFilterCursor = await services.Notifications.ListAsync(
            owner.Id,
            "unread",
            50,
            firstPage.NextCursor);
        Assert.True(crossFilterCursor.InvalidCursor);

        var invalidCursor = await services.Notifications.ListAsync(owner.Id, "all", 50, "not-a-cursor");
        Assert.True(invalidCursor.InvalidCursor);

        var otherPage = await services.Notifications.ListAsync(other.Id, "all", 50, null);
        Assert.Equal(otherNotification.Id, Assert.Single(otherPage.Items).Id);
        Assert.Equal(1, otherPage.UnreadCount);
    }

    [DatabaseFact]
    public async Task Read_and_read_all_are_idempotent_and_owner_scoped()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var owner = ServiceTestSupport.NewUser("notify-read-owner");
        var other = ServiceTestSupport.NewUser("notify-read-other");
        db.Users.AddRange(owner, other);
        var first = NewNotification(
            owner.Id,
            NotificationTypes.Reply,
            new { summary = "第一条通知" },
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = NewNotification(
            owner.Id,
            NotificationTypes.PostApproved,
            new { summary = "第二条通知" },
            DateTimeOffset.UtcNow.AddMinutes(-2));
        var alreadyRead = NewNotification(
            owner.Id,
            NotificationTypes.CommentApproved,
            new { summary = "第三条通知" },
            DateTimeOffset.UtcNow.AddMinutes(-3));
        alreadyRead.ReadAt = DateTimeOffset.UtcNow;
        var otherNotification = NewNotification(
            other.Id,
            NotificationTypes.Reply,
            new { summary = "其他人的通知" },
            DateTimeOffset.UtcNow.AddMinutes(-1));
        db.Notifications.AddRange(first, second, alreadyRead, otherNotification);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var read = await services.Notifications.MarkReadAsync(owner.Id, first.Id);
        Assert.True(read.Read);
        Assert.Equal(1, read.UnreadCount);

        var readAgain = await services.Notifications.MarkReadAsync(owner.Id, first.Id);
        Assert.True(readAgain.Read);
        Assert.Equal(1, readAgain.UnreadCount);

        var notOwner = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Notifications.MarkReadAsync(owner.Id, otherNotification.Id));
        Assert.Equal(ApiErrorCodes.NotFound, notOwner.Code);

        var readAll = await services.Notifications.MarkAllReadAsync(owner.Id);
        Assert.True(readAll.Read);
        Assert.Equal(0, readAll.UnreadCount);

        var readAllAgain = await services.Notifications.MarkAllReadAsync(owner.Id);
        Assert.Equal(0, readAllAgain.UnreadCount);

        db.ChangeTracker.Clear();
        Assert.All(
            await db.Notifications.AsNoTracking()
                .Where(notification => notification.UserId == owner.Id)
                .ToListAsync(),
            notification => Assert.NotNull(notification.ReadAt));
        var storedOther = await db.Notifications.AsNoTracking()
            .SingleAsync(notification => notification.Id == otherNotification.Id);
        Assert.Null(storedOther.ReadAt);
    }

    [DatabaseFact]
    public async Task Notification_access_follows_account_state()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var pending = ServiceTestSupport.NewUser("notify-state-pending", activated: false);
        var muted = ServiceTestSupport.NewUser(
            "notify-state-muted",
            status: UserStatuses.Muted,
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
            statusReason: "测试禁言");
        var banned = ServiceTestSupport.NewUser(
            "notify-state-banned",
            status: UserStatuses.Banned,
            statusReason: "测试封禁");
        var deleted = ServiceTestSupport.NewUser("notify-state-deleted");
        deleted.Status = UserStatuses.Deleted;
        deleted.DeletedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var member = ServiceTestSupport.NewUser("notify-state-member");
        var admin = ServiceTestSupport.NewUser("notify-state-admin", UserRoles.Admin);
        db.Users.AddRange(pending, muted, banned, deleted, member, admin);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);

        var pendingError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Notifications.ListAsync(pending.Id, "all", 20, null));
        Assert.Equal(ApiErrorCodes.Forbidden, pendingError.Code);
        var pendingDetails = JsonSerializer.SerializeToElement(pendingError.Details);
        Assert.Equal("activation_required", pendingDetails.GetProperty("reason").GetString());

        var mutedPage = await services.Notifications.ListAsync(muted.Id, "all", 20, null);
        Assert.Empty(mutedPage.Items);

        var bannedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Notifications.ListAsync(banned.Id, "all", 20, null));
        Assert.Equal(ApiErrorCodes.AccountBanned, bannedError.Code);

        var deletedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Notifications.ListAsync(deleted.Id, "all", 20, null));
        Assert.Equal(ApiErrorCodes.Forbidden, deletedError.Code);
        var deletedDetails = JsonSerializer.SerializeToElement(deletedError.Details);
        Assert.Equal("account_deleted", deletedDetails.GetProperty("reason").GetString());

        var memberPage = await services.Notifications.ListAsync(member.Id, "all", 20, null);
        Assert.Empty(memberPage.Items);
        var adminPage = await services.Notifications.ListAsync(admin.Id, "all", 20, null);
        Assert.Empty(adminPage.Items);

        var mutedRead = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.Notifications.MarkReadAsync(muted.Id, Guid.CreateVersion7()));
        Assert.Equal(ApiErrorCodes.NotFound, mutedRead.Code);
    }

    [DatabaseFact]
    public async Task Replies_create_notification_for_other_parent_author_only()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var postAuthor = ServiceTestSupport.NewUser("notify-reply-author");
        var parentAuthor = ServiceTestSupport.NewUser("notify-reply-parent");
        var replier = ServiceTestSupport.NewUser("notify-reply-member");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(postAuthor.Id, category.Id);
        var parent = ServiceTestSupport.NewComment(post.Id, parentAuthor.Id, body: "父评论内容");
        var ownComment = ServiceTestSupport.NewComment(post.Id, replier.Id, body: "自己的评论");
        db.Users.AddRange(postAuthor, parentAuthor, replier);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.AddRange(parent, ownComment);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var reply = await services.Comments.CreateAsync(
            replier.Id,
            post.Id,
            new CommentCreateCommand("回复父评论的内容", parent.Id),
            ServiceTestSupport.NewAudit(replier.Id));

        var notification = await db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == parentAuthor.Id
                && candidate.Type == NotificationTypes.Reply);
        Assert.Equal($"reply:{reply.Id:N}", notification.DedupeKey);
        var payload = notification.PayloadJson.RootElement;
        Assert.Equal("comment", payload.GetProperty("targetType").GetString());
        Assert.Equal(reply.Id, payload.GetProperty("targetId").GetGuid());
        Assert.Equal($"/posts/{post.Id}?comment={reply.Id}", payload.GetProperty("targetUrl").GetString());

        var parentPage = await services.Notifications.ListAsync(parentAuthor.Id, "all", 20, null);
        var view = Assert.Single(parentPage.Items);
        Assert.Equal(NotificationTypes.Reply, view.Type);
        Assert.StartsWith($"/posts/{post.Id}", view.Payload.TargetUrl, StringComparison.Ordinal);

        await services.Comments.CreateAsync(
            replier.Id,
            post.Id,
            new CommentCreateCommand("回复自己的评论内容", ownComment.Id),
            ServiceTestSupport.NewAudit(replier.Id));
        Assert.Equal(0, await db.Notifications.CountAsync(candidate =>
            candidate.UserId == replier.Id && candidate.Type == NotificationTypes.Reply));
    }

    private static Notification NewNotification(
        Guid userId,
        string type,
        object payload,
        DateTimeOffset createdAt)
    {
        return new Notification
        {
            UserId = userId,
            Type = type,
            PayloadJson = JsonSerializer.SerializeToDocument(payload),
            DedupeKey = $"seed:{Guid.NewGuid():N}",
            OccurrenceCount = 1,
            LastOccurredAt = createdAt,
            CreatedAt = createdAt
        };
    }
}