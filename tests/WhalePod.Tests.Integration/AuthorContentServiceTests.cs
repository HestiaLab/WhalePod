using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class AuthorContentServiceTests
{
    [DatabaseFact]
    public async Task My_lists_return_only_own_approved_pending_and_rejected_content()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("me-author");
        var other = ServiceTestSupport.NewUser("me-other");
        var admin = ServiceTestSupport.NewUser("me-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(member, other, admin);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var othersPost = ServiceTestSupport.NewPost(
            other.Id,
            category.Id,
            title: "其他用户的公开帖子标题");
        var approved = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Approved,
            "作者已通过帖子标题");
        var pending = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Pending,
            "作者待审帖子标题");
        var rejected = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Rejected,
            "作者已驳回帖子标题");
        var hidden = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Hidden,
            "作者隐藏帖子标题");
        var deletedStatus = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Deleted,
            "作者删除状态帖子标题");
        var softDeleted = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Approved,
            "作者软删除帖子标题");
        softDeleted.DeletedAt = DateTimeOffset.UtcNow;
        var adminPost = ServiceTestSupport.NewPost(
            admin.Id,
            category.Id,
            ModerationStatuses.Pending,
            "管理员自己的待审帖子");
        db.Posts.AddRange(othersPost, approved, pending, rejected, hidden, deletedStatus, softDeleted, adminPost);

        var approvedComment = ServiceTestSupport.NewComment(
            othersPost.Id,
            member.Id,
            ModerationStatuses.Approved,
            body: "作者已通过评论");
        var pendingComment = ServiceTestSupport.NewComment(
            othersPost.Id,
            member.Id,
            ModerationStatuses.Pending,
            body: "作者待审评论");
        var rejectedComment = ServiceTestSupport.NewComment(
            othersPost.Id,
            member.Id,
            ModerationStatuses.Rejected,
            body: "作者已驳回评论");
        var hiddenComment = ServiceTestSupport.NewComment(
            othersPost.Id,
            member.Id,
            ModerationStatuses.Hidden,
            body: "作者隐藏评论");
        var deletedComment = ServiceTestSupport.NewComment(
            othersPost.Id,
            member.Id,
            ModerationStatuses.Deleted,
            body: "作者删除评论");
        var otherComment = ServiceTestSupport.NewComment(
            othersPost.Id,
            other.Id,
            ModerationStatuses.Pending,
            body: "其他用户的评论");
        db.Comments.AddRange(
            approvedComment,
            pendingComment,
            rejectedComment,
            hiddenComment,
            deletedComment,
            otherComment);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var posts = await services.AuthorContent.GetMyPostsAsync(member.Id, null, 20, null);
        Assert.False(posts.InvalidCursor);
        Guid[] expectedPostIds = [pending.Id, rejected.Id, approved.Id];
        Assert.Equal(
            expectedPostIds.OrderBy(id => id).ToArray(),
            posts.Items.Select(item => item.Id).OrderBy(id => id).ToArray());
        Assert.DoesNotContain(posts.Items, item => item.Id == hidden.Id || item.Id == deletedStatus.Id);
        Assert.DoesNotContain(posts.Items, item => item.Id == softDeleted.Id);
        Assert.All(posts.Items, item => Assert.Equal(member.Id, item.Author.Id));

        var filteredPosts = await services.AuthorContent.GetMyPostsAsync(
            member.Id,
            ModerationStatuses.Rejected,
            20,
            null);
        Assert.Equal(rejected.Id, Assert.Single(filteredPosts.Items).Id);

        var otherPosts = await services.AuthorContent.GetMyPostsAsync(other.Id, null, 20, null);
        Assert.Equal(othersPost.Id, Assert.Single(otherPosts.Items).Id);

        var adminPosts = await services.AuthorContent.GetMyPostsAsync(admin.Id, null, 20, null);
        Assert.Equal(adminPost.Id, Assert.Single(adminPosts.Items).Id);

        var comments = await services.AuthorContent.GetMyCommentsAsync(member.Id, null, 20, null);
        Assert.Equal(3, comments.Items.Count);
        Assert.Contains(comments.Items, item => item.Id == approvedComment.Id);
        Assert.Contains(comments.Items, item => item.Id == pendingComment.Id);
        Assert.Contains(comments.Items, item => item.Id == rejectedComment.Id);
        Assert.DoesNotContain(comments.Items, item => item.Id == hiddenComment.Id);
        Assert.DoesNotContain(comments.Items, item => item.Id == deletedComment.Id);
        Assert.All(comments.Items, item =>
        {
            Assert.False(item.IsDeleted);
            Assert.NotNull(item.BodyMd);
        });

        var pendingComments = await services.AuthorContent.GetMyCommentsAsync(
            member.Id,
            ModerationStatuses.Pending,
            20,
            null);
        Assert.Equal(pendingComment.Id, Assert.Single(pendingComments.Items).Id);
        var otherComments = await services.AuthorContent.GetMyCommentsAsync(other.Id, null, 20, null);
        Assert.Equal(otherComment.Id, Assert.Single(otherComments.Items).Id);
        Assert.Empty((await services.AuthorContent.GetMyCommentsAsync(
            admin.Id,
            null,
            20,
            null)).Items);
    }

    [DatabaseFact]
    public async Task My_lists_enforce_account_state_and_allow_muted_reads()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("me-state-member");
        var pending = ServiceTestSupport.NewUser("me-state-pending", activated: false);
        var muted = ServiceTestSupport.NewUser(
            "me-state-muted",
            status: UserStatuses.Muted,
            statusReason: "测试禁言",
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1));
        var banned = ServiceTestSupport.NewUser(
            "me-state-banned",
            status: UserStatuses.Banned,
            statusReason: "测试封禁");
        var deleted = ServiceTestSupport.NewUser("me-state-deleted", status: UserStatuses.Deleted);
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        var admin = ServiceTestSupport.NewUser("me-state-admin", UserRoles.Admin);
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(member, pending, muted, banned, deleted, admin);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var users = new[] { member, pending, muted, banned, deleted, admin };
        foreach (var user in users)
        {
            db.Posts.Add(ServiceTestSupport.NewPost(
                user.Id,
                category.Id,
                ModerationStatuses.Pending,
                $"{user.Handle} 的待审帖子标题"));
        }

        await db.SaveChangesAsync();
        var services = ServiceTestSupport.CreateServices(db);

        var anonymous = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AuthorContent.GetMyPostsAsync(Guid.NewGuid(), null, 20, null));
        Assert.Equal(ApiErrorCodes.AuthRequired, anonymous.Code);
        Assert.Equal(401, anonymous.StatusCode);

        var pendingError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AuthorContent.GetMyPostsAsync(pending.Id, null, 20, null));
        Assert.Equal(ApiErrorCodes.Forbidden, pendingError.Code);
        Assert.Equal("activation_required", DetailsReason(pendingError));

        var bannedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AuthorContent.GetMyPostsAsync(banned.Id, null, 20, null));
        Assert.Equal(ApiErrorCodes.AccountBanned, bannedError.Code);
        Assert.Equal(403, bannedError.StatusCode);

        var deletedError = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AuthorContent.GetMyPostsAsync(deleted.Id, null, 20, null));
        Assert.Equal(ApiErrorCodes.Forbidden, deletedError.Code);
        Assert.Equal("account_deleted", DetailsReason(deletedError));

        var mutedPosts = await services.AuthorContent.GetMyPostsAsync(muted.Id, null, 20, null);
        Assert.Single(mutedPosts.Items);
        var memberPosts = await services.AuthorContent.GetMyPostsAsync(member.Id, null, 20, null);
        Assert.Single(memberPosts.Items);
        var adminPosts = await services.AuthorContent.GetMyPostsAsync(admin.Id, null, 20, null);
        Assert.Single(adminPosts.Items);

        var invalidStatus = await Assert.ThrowsAsync<ApiProblemException>(
            () => services.AuthorContent.GetMyCommentsAsync(member.Id, "hidden", 20, null));
        Assert.Equal(ApiErrorCodes.ValidationFailed, invalidStatus.Code);
    }

    [DatabaseFact]
    public async Task My_lists_paginate_and_bind_cursors_to_the_active_filter()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("me-page-member");
        var category = ServiceTestSupport.NewCategory();
        db.Users.Add(member);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var baseTime = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var posts = new List<Post>();
        for (var index = 0; index < 6; index++)
        {
            var status = index % 2 == 0 ? ModerationStatuses.Approved : ModerationStatuses.Pending;
            var post = ServiceTestSupport.NewPost(
                member.Id,
                category.Id,
                status,
                $"分页测试帖子标题 {index}");
            post.CreatedAt = baseTime.AddMinutes(index);
            post.UpdatedAt = baseTime.AddMinutes(index);
            post.LastActivityAt = baseTime.AddMinutes(index);
            posts.Add(post);
        }

        db.Posts.AddRange(posts);
        await db.SaveChangesAsync();

        var parent = posts[0];
        var comments = new List<Comment>();
        for (var index = 0; index < 5; index++)
        {
            var comment = ServiceTestSupport.NewComment(
                parent.Id,
                member.Id,
                index % 2 == 0 ? ModerationStatuses.Approved : ModerationStatuses.Pending,
                body: $"分页测试评论 {index}");
            comment.CreatedAt = baseTime.AddMinutes(index);
            comment.UpdatedAt = baseTime.AddMinutes(index);
            comments.Add(comment);
        }

        db.Comments.AddRange(comments);
        await db.SaveChangesAsync();

        var services = ServiceTestSupport.CreateServices(db);
        var postPages = await WalkAsync(
            cursor => services.AuthorContent.GetMyPostsAsync(member.Id, null, 2, cursor),
            page => page.NextCursor,
            page => page.Items.Select(item => item.Id).ToList(),
            page => page.InvalidCursor);
        Assert.Equal(posts.Count, postPages.Count);
        Assert.Equal(postPages.Count, postPages.Distinct().Count());
        var expectedPostOrder = posts
            .OrderByDescending(post => post.UpdatedAt)
            .ThenByDescending(post => post.Id)
            .Select(post => post.Id)
            .ToList();
        Assert.Equal(expectedPostOrder, postPages);

        var firstPostPage = await services.AuthorContent.GetMyPostsAsync(member.Id, null, 2, null);
        Assert.NotNull(firstPostPage.NextCursor);
        var stalePostCursor = await services.AuthorContent.GetMyPostsAsync(
            member.Id,
            ModerationStatuses.Pending,
            2,
            firstPostPage.NextCursor);
        Assert.True(stalePostCursor.InvalidCursor);
        var garbageCursor = await services.AuthorContent.GetMyPostsAsync(member.Id, null, 2, "not-a-cursor");
        Assert.True(garbageCursor.InvalidCursor);

        var pendingPosts = await WalkAsync(
            cursor => services.AuthorContent.GetMyPostsAsync(member.Id, ModerationStatuses.Pending, 2, cursor),
            page => page.NextCursor,
            page => page.Items.Select(item => item.Id).ToList(),
            page => page.InvalidCursor);
        Assert.Equal(3, pendingPosts.Count);
        Assert.Equal(pendingPosts.Count, pendingPosts.Distinct().Count());

        var commentPages = await WalkAsync(
            cursor => services.AuthorContent.GetMyCommentsAsync(member.Id, null, 2, cursor),
            page => page.NextCursor,
            page => page.Items.Select(item => item.Id).ToList(),
            page => page.InvalidCursor);
        Assert.Equal(comments.Count, commentPages.Count);
        Assert.Equal(commentPages.Count, commentPages.Distinct().Count());

        var firstCommentPage = await services.AuthorContent.GetMyCommentsAsync(member.Id, null, 2, null);
        Assert.NotNull(firstCommentPage.NextCursor);
        var staleCommentCursor = await services.AuthorContent.GetMyCommentsAsync(
            member.Id,
            ModerationStatuses.Approved,
            2,
            firstCommentPage.NextCursor);
        Assert.True(staleCommentCursor.InvalidCursor);

        var pendingCommentPages = await WalkAsync(
            cursor => services.AuthorContent.GetMyCommentsAsync(member.Id, ModerationStatuses.Pending, 2, cursor),
            page => page.NextCursor,
            page => page.Items.Select(item => item.Id).ToList(),
            page => page.InvalidCursor);
        Assert.Equal(2, pendingCommentPages.Count);
    }

    private static async Task<List<Guid>> WalkAsync<TPage>(
        Func<string?, Task<TPage>> fetch,
        Func<TPage, string?> nextCursor,
        Func<TPage, List<Guid>> itemIds,
        Func<TPage, bool> invalidCursor)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        for (var pageIndex = 0; pageIndex < 20; pageIndex++)
        {
            var page = await fetch(cursor);
            Assert.False(invalidCursor(page));
            ids.AddRange(itemIds(page));
            cursor = nextCursor(page);
            if (cursor is null)
            {
                return ids;
            }
        }

        throw new InvalidOperationException("Cursor pagination did not terminate.");
    }

    private static string? DetailsReason(ApiProblemException exception)
    {
        return exception.Details?.GetType().GetProperty("reason")?.GetValue(exception.Details) as string;
    }
}

[Collection("api-http")]
public sealed class AuthorContentHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Me_endpoints_require_auth_apply_account_state_and_only_show_own_content()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("me-http-member");
        member.Handle = $"me-http-member-{suffix}";
        var other = ServiceTestSupport.NewUser("me-http-other");
        other.Handle = $"me-http-other-{suffix}";
        var admin = ServiceTestSupport.NewUser("me-http-admin", UserRoles.Admin);
        admin.Handle = $"me-http-admin-{suffix}";
        var pending = ServiceTestSupport.NewUser("me-http-pending", activated: false);
        pending.Handle = $"me-http-pending-{suffix}";
        var muted = ServiceTestSupport.NewUser(
            "me-http-muted",
            status: UserStatuses.Muted,
            statusReason: "HTTP 禁言",
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1));
        muted.Handle = $"me-http-muted-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(member, other, admin, pending, muted);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var memberPending = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Pending,
            $"HTTP 作者待审帖子 {suffix}");
        var memberRejected = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Rejected,
            $"HTTP 作者驳回帖子 {suffix}");
        var memberApproved = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Approved,
            $"HTTP 作者通过帖子 {suffix}");
        var memberHidden = ServiceTestSupport.NewPost(
            member.Id,
            category.Id,
            ModerationStatuses.Hidden,
            $"HTTP 作者隐藏帖子 {suffix}");
        var otherPost = ServiceTestSupport.NewPost(
            other.Id,
            category.Id,
            ModerationStatuses.Approved,
            $"HTTP 其他用户帖子 {suffix}");
        var adminPost = ServiceTestSupport.NewPost(
            admin.Id,
            category.Id,
            ModerationStatuses.Pending,
            $"HTTP 管理员帖子 {suffix}");
        var mutedPost = ServiceTestSupport.NewPost(
            muted.Id,
            category.Id,
            ModerationStatuses.Pending,
            $"HTTP 禁言用户帖子 {suffix}");
        var pendingPost = ServiceTestSupport.NewPost(
            pending.Id,
            category.Id,
            ModerationStatuses.Pending,
            $"HTTP 待激活用户帖子 {suffix}");
        db.Posts.AddRange(
            memberPending,
            memberRejected,
            memberApproved,
            memberHidden,
            otherPost,
            adminPost,
            mutedPost,
            pendingPost);

        var memberComment = ServiceTestSupport.NewComment(
            otherPost.Id,
            member.Id,
            ModerationStatuses.Rejected,
            body: "HTTP 作者驳回评论");
        var memberHiddenComment = ServiceTestSupport.NewComment(
            otherPost.Id,
            member.Id,
            ModerationStatuses.Hidden,
            body: "HTTP 作者隐藏评论");
        db.Comments.AddRange(memberComment, memberHiddenComment);
        await db.SaveChangesAsync();

        var client = environment.ApiFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using (var anonymous = await client.GetAsync("/api/v1/me/posts"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("AUTH_REQUIRED", await ReadCodeAsync(anonymous));
        }

        var pendingSession = await environment.LoginAsync(pending.Handle);
        using (var pendingResponse = await GetAsync(client, "/api/v1/me/posts", pendingSession))
        {
            Assert.Equal(HttpStatusCode.Forbidden, pendingResponse.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(pendingResponse));
            using var document = JsonDocument.Parse(await pendingResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "activation_required",
                document.RootElement.GetProperty("details").GetProperty("reason").GetString());
        }

        var mutedSession = await environment.LoginAsync(muted.Handle);
        using (var mutedResponse = await GetAsync(client, "/api/v1/me/posts", mutedSession))
        {
            Assert.Equal(HttpStatusCode.OK, mutedResponse.StatusCode);
            using var document = JsonDocument.Parse(await mutedResponse.Content.ReadAsStringAsync());
            var items = document.RootElement.GetProperty("data").GetProperty("items");
            Assert.Equal(mutedPost.Id, Assert.Single(items.EnumerateArray()).GetProperty("id").GetGuid());
        }

        var memberSession = await environment.LoginAsync(member.Handle);
        using (var memberResponse = await GetAsync(client, "/api/v1/me/posts", memberSession))
        {
            Assert.Equal(HttpStatusCode.OK, memberResponse.StatusCode);
            using var document = JsonDocument.Parse(await memberResponse.Content.ReadAsStringAsync());
            var items = document.RootElement.GetProperty("data").GetProperty("items");
            var ids = items.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
            Assert.Equal(3, ids.Count);
            Assert.Contains(memberPending.Id, ids);
            Assert.Contains(memberRejected.Id, ids);
            Assert.Contains(memberApproved.Id, ids);
            Assert.DoesNotContain(memberHidden.Id, ids);
            Assert.DoesNotContain(otherPost.Id, ids);
        }

        using (var rejectedResponse = await GetAsync(
                   client,
                   "/api/v1/me/posts?status=rejected",
                   memberSession))
        {
            Assert.Equal(HttpStatusCode.OK, rejectedResponse.StatusCode);
            using var document = JsonDocument.Parse(await rejectedResponse.Content.ReadAsStringAsync());
            var item = Assert.Single(
                document.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
            Assert.Equal(memberRejected.Id, item.GetProperty("id").GetGuid());
            Assert.Equal("rejected", item.GetProperty("moderationStatus").GetString());
        }

        using (var commentsResponse = await GetAsync(
                   client,
                   "/api/v1/me/comments?limit=1",
                   memberSession))
        {
            Assert.Equal(HttpStatusCode.OK, commentsResponse.StatusCode);
            using var document = JsonDocument.Parse(await commentsResponse.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            var first = Assert.Single(data.GetProperty("items").EnumerateArray());
            Assert.Equal(memberComment.Id, first.GetProperty("id").GetGuid());
            var cursor = data.GetProperty("nextCursor").GetString();
            Assert.Null(cursor);
        }

        using (var invalidStatus = await GetAsync(
                   client,
                   "/api/v1/me/posts?status=hidden",
                   memberSession))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
            Assert.Equal("VALIDATION_FAILED", await ReadCodeAsync(invalidStatus));
        }

        using (var invalidLimit = await GetAsync(client, "/api/v1/me/posts?limit=0", memberSession))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
            Assert.Equal("VALIDATION_FAILED", await ReadCodeAsync(invalidLimit));
        }

        using (var invalidCursor = await GetAsync(
                   client,
                   "/api/v1/me/posts?cursor=not-a-cursor",
                   memberSession))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
            Assert.Equal("VALIDATION_FAILED", await ReadCodeAsync(invalidCursor));
        }

        using (var firstPage = await GetAsync(client, "/api/v1/me/posts?limit=1", memberSession))
        {
            using var document = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            var cursor = document.RootElement
                .GetProperty("data")
                .GetProperty("nextCursor")
                .GetString();
            Assert.NotNull(cursor);
            using var staleCursor = await GetAsync(
                client,
                $"/api/v1/me/posts?status=pending&cursor={Uri.EscapeDataString(cursor!)}",
                memberSession);
            Assert.Equal(HttpStatusCode.BadRequest, staleCursor.StatusCode);
            Assert.Equal("VALIDATION_FAILED", await ReadCodeAsync(staleCursor));
        }

        var adminSession = await environment.LoginAsync(admin.Handle);
        using (var adminResponse = await GetAsync(client, "/api/v1/me/posts", adminSession))
        {
            Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
            using var document = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync());
            var item = Assert.Single(
                document.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
            Assert.Equal(adminPost.Id, item.GetProperty("id").GetGuid());
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string? sessionCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (sessionCookie is not null)
        {
            request.Headers.Add("Cookie", sessionCookie);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return document.RootElement.GetProperty("code").GetString();
    }
}