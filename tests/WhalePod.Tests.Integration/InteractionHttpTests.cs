using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class InteractionHttpTests(ApiHttpEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [DatabaseFact]
    public async Task Likes_acceptance_and_notifications_enforce_auth_csrf_and_ownership()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var author = ServiceTestSupport.NewUser("http-like-author");
        author.Handle = $"http-like-author-{suffix}";
        var member = ServiceTestSupport.NewUser("http-like-member");
        member.Handle = $"http-like-member-{suffix}";
        var outsider = ServiceTestSupport.NewUser("http-like-outsider");
        outsider.Handle = $"http-like-outsider-{suffix}";
        var pending = ServiceTestSupport.NewUser("http-like-pending", activated: false);
        pending.Handle = $"http-like-pending-{suffix}";
        var muted = ServiceTestSupport.NewUser(
            "http-like-muted",
            status: UserStatuses.Muted,
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
            statusReason: "HTTP 测试禁言");
        muted.Handle = $"http-like-muted-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        var comment = ServiceTestSupport.NewComment(post.Id, member.Id, body: "HTTP 测试公开评论");
        db.Users.AddRange(author, member, outsider, pending, muted);
        db.Categories.Add(category);
        db.Posts.Add(post);
        db.Comments.Add(comment);
        await db.SaveChangesAsync();

        var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using (var anonymous = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/posts/{post.Id}/like",
                   new { liked = true },
                   null,
                   null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("AUTH_REQUIRED", await ReadCodeAsync(anonymous));
        }

        var memberSession = await environment.LoginAsync(member.Handle);
        var (memberCsrf, memberAntiforgery) = await environment.ApiFactory.GetCsrfAsync(memberSession);

        using (var missingCsrf = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/posts/{post.Id}/like",
                   new { liked = true },
                   memberSession,
                   null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(missingCsrf));
        }

        Assert.Equal(0, await db.PostLikes.CountAsync(like => like.PostId == post.Id));

        using (var missingLiked = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/posts/{post.Id}/like",
                   new { },
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingLiked.StatusCode);
            Assert.Equal("VALIDATION_FAILED", await ReadCodeAsync(missingLiked));
            using var document = JsonDocument.Parse(await missingLiked.Content.ReadAsStringAsync());
            Assert.Equal(
                "liked",
                document.RootElement.GetProperty("details").GetProperty("issues")[0].GetProperty("field").GetString());
        }

        using (var liked = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/posts/{post.Id}/like",
                   new { liked = true },
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, liked.StatusCode);
            using var document = JsonDocument.Parse(await liked.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("liked").GetBoolean());
            Assert.Equal(1, data.GetProperty("likeCount").GetInt32());
        }

        using (var emptyNotifications = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/notifications",
                   null,
                   memberSession,
                   null))
        {
            Assert.Equal(HttpStatusCode.OK, emptyNotifications.StatusCode);
            using var document = JsonDocument.Parse(await emptyNotifications.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.Empty(data.GetProperty("items").EnumerateArray());
            Assert.Equal(0, data.GetProperty("unreadCount").GetInt32());
        }

        var authorSession = await environment.LoginAsync(author.Handle);
        var (authorCsrf, authorAntiforgery) = await environment.ApiFactory.GetCsrfAsync(authorSession);
        using (var selfLike = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/posts/{post.Id}/like",
                   new { liked = true },
                   authorSession,
                   authorCsrf,
                   authorAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, selfLike.StatusCode);
            using var document = JsonDocument.Parse(await selfLike.Content.ReadAsStringAsync());
            Assert.Equal(
                "self_like",
                document.RootElement.GetProperty("details").GetProperty("reason").GetString());
        }

        using (var memberAccept = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/comments/{comment.Id}/accept",
                   null,
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, memberAccept.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(memberAccept));
        }

        using (var accepted = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/comments/{comment.Id}/accept",
                   null,
                   authorSession,
                   authorCsrf,
                   authorAntiforgery))
        {
            var acceptedBody = await accepted.Content.ReadAsStringAsync();
            Assert.True(accepted.StatusCode == HttpStatusCode.OK, acceptedBody);
            using var document = JsonDocument.Parse(acceptedBody);
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(post.Id, data.GetProperty("postId").GetGuid());
            Assert.Equal(comment.Id, data.GetProperty("acceptedCommentId").GetGuid());
            Assert.True(data.GetProperty("isSolved").GetBoolean());
        }

        db.ChangeTracker.Clear();
        var storedPost = await db.Posts.SingleAsync(candidate => candidate.Id == post.Id);
        Assert.Equal(comment.Id, storedPost.AcceptedCommentId);
        Assert.True(storedPost.IsSolved);

        Guid notificationId;
        using (var list = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/notifications?filter=unread",
                   null,
                   memberSession,
                   null))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            var item = Assert.Single(data.GetProperty("items").EnumerateArray());
            Assert.Equal(NotificationTypes.CommentAccepted, item.GetProperty("type").GetString());
            Assert.Equal(1, data.GetProperty("unreadCount").GetInt32());
            var payload = item.GetProperty("payload");
            Assert.Equal("comment", payload.GetProperty("targetType").GetString());
            Assert.Equal(comment.Id, payload.GetProperty("targetId").GetGuid());
            Assert.Equal(
                $"/posts/{post.Id}?comment={comment.Id}",
                payload.GetProperty("targetUrl").GetString());
            notificationId = item.GetProperty("id").GetGuid();
        }

        using (var missingCsrfRead = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/notifications/{notificationId}/read",
                   null,
                   memberSession,
                   null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrfRead.StatusCode);
        }

        db.ChangeTracker.Clear();
        Assert.Null((await db.Notifications.SingleAsync(candidate => candidate.Id == notificationId)).ReadAt);

        using (var read = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/notifications/{notificationId}/read",
                   null,
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("read").GetBoolean());
            Assert.Equal(0, data.GetProperty("unreadCount").GetInt32());
        }

        using (var readAgain = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/notifications/{notificationId}/read",
                   null,
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, readAgain.StatusCode);
            using var document = JsonDocument.Parse(await readAgain.Content.ReadAsStringAsync());
            Assert.Equal(
                0,
                document.RootElement.GetProperty("data").GetProperty("unreadCount").GetInt32());
        }

        var outsiderSession = await environment.LoginAsync(outsider.Handle);
        var (outsiderCsrf, outsiderAntiforgery) = await environment.ApiFactory.GetCsrfAsync(outsiderSession);
        using (var notOwnerRead = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/notifications/{notificationId}/read",
                   null,
                   outsiderSession,
                   outsiderCsrf,
                   outsiderAntiforgery))
        {
            Assert.Equal(HttpStatusCode.NotFound, notOwnerRead.StatusCode);
            Assert.Equal("NOT_FOUND", await ReadCodeAsync(notOwnerRead));
        }

        using (var readAll = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/notifications/read-all",
                   null,
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, readAll.StatusCode);
            using var document = JsonDocument.Parse(await readAll.Content.ReadAsStringAsync());
            Assert.Equal(
                0,
                document.RootElement.GetProperty("data").GetProperty("unreadCount").GetInt32());
        }

        var pendingSession = await environment.LoginAsync(pending.Handle);
        using (var pendingList = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/v1/notifications",
                   null,
                   pendingSession,
                   null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, pendingList.StatusCode);
            using var document = JsonDocument.Parse(await pendingList.Content.ReadAsStringAsync());
            Assert.Equal(
                "activation_required",
                document.RootElement.GetProperty("details").GetProperty("reason").GetString());
        }

        var mutedSession = await environment.LoginAsync(muted.Handle);
        var (mutedCsrf, mutedAntiforgery) = await environment.ApiFactory.GetCsrfAsync(mutedSession);
        using (var mutedLike = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/v1/posts/{post.Id}/like",
                   new { liked = true },
                   mutedSession,
                   mutedCsrf,
                   mutedAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, mutedLike.StatusCode);
            Assert.Equal("ACCOUNT_MUTED", await ReadCodeAsync(mutedLike));
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body,
        string? sessionCookie,
        string? csrfToken,
        string? antiforgeryCookie = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        if (sessionCookie is not null)
        {
            request.Headers.Add(
                "Cookie",
                antiforgeryCookie is null
                    ? sessionCookie
                    : $"{sessionCookie}; {antiforgeryCookie}");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrfToken);
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