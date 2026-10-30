using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class CommentWebBehaviorTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Comment_failure_keeps_draft_and_second_page_reply_target_is_loaded_independently()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var author = ServiceTestSupport.NewUser("comment-ui-author");
        author.Handle = $"comment-ui-author-{suffix}";
        var commenter = ServiceTestSupport.NewUser("comment-ui-commenter");
        commenter.Handle = $"comment-ui-commenter-{suffix}";
        var member = ServiceTestSupport.NewUser("comment-ui-member");
        member.Handle = $"comment-ui-member-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id, title: $"评论交互帖子 {suffix}");
        var now = DateTimeOffset.UtcNow;
        var comments = Enumerable.Range(0, 51)
            .Select(index => ServiceTestSupport.NewComment(
                post.Id,
                commenter.Id,
                body: $"分页评论 {index} {suffix}"))
            .ToList();
        for (var index = 0; index < comments.Count; index++)
        {
            comments[index].CreatedAt = now.AddMinutes(-index);
            comments[index].UpdatedAt = now.AddMinutes(-index);
        }

        var deepRoot = ServiceTestSupport.NewComment(post.Id, commenter.Id, body: $"深层评论 1 {suffix}");
        var deep = new List<Comment> { deepRoot };
        for (var depth = 2; depth <= 8; depth++)
        {
            deep.Add(ServiceTestSupport.NewComment(
                post.Id,
                commenter.Id,
                body: $"深层评论 {depth} {suffix}",
                parentId: deep[^1].Id));
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.AddRange(author, commenter, member);
            db.Categories.Add(category);
            db.Posts.Add(post);
            db.Comments.AddRange(comments);
            db.Comments.AddRange(deep);
            await db.SaveChangesAsync();
        }

        Guid secondPageCommentId;
        string nextCursor;
        await using (var db = ServiceTestSupport.CreateContext())
        {
            var content = new PublicContentService(db);
            var firstPage = await content.GetCommentsAsync(post.Id, "latest", 50, null);
            Assert.NotNull(firstPage);
            Assert.NotNull(firstPage!.NextCursor);
            nextCursor = firstPage.NextCursor!;
            var secondPage = await content.GetCommentsAsync(post.Id, "latest", 50, nextCursor);
            Assert.NotNull(secondPage);
            Assert.NotEmpty(secondPage!.Items);
            secondPageCommentId = secondPage.Items[0].Id;
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(member.Handle);
        var form = await WebFormTestSupport.LoadFormAsync(client, $"/posts/{post.Id}", cookie);

        var replyBody = $"第二页回复 {suffix}";
        using (var reply = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{post.Id}/comments",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = form.Token,
                       ["bodyMd"] = replyBody,
                       ["parentId"] = secondPageCommentId.ToString()
                   },
                   cookie,
                   form.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, reply.StatusCode);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var created = await db.Comments.AsNoTracking().SingleAsync(comment =>
                comment.AuthorId == member.Id && comment.BodyMd == replyBody);
            Assert.Equal(secondPageCommentId, created.ParentId);
            Assert.Equal(ModerationStatuses.Pending, created.ModerationStatus);
        }

        using (var replyContext = await GetWithCookieAsync(
                   client,
                   $"/posts/{post.Id}?replyTo={secondPageCommentId}&sort=latest&cursor={Uri.EscapeDataString(nextCursor)}",
                   cookie))
        {
            Assert.Equal(HttpStatusCode.OK, replyContext.StatusCode);
            var html = WebUtility.HtmlDecode(await replyContext.Content.ReadAsStringAsync());
            Assert.DoesNotContain("要回复的评论不存在", html, StringComparison.Ordinal);
            Assert.Contains("回复", html, StringComparison.Ordinal);
        }

        using (var detail = await GetWithCookieAsync(client, $"/posts/{post.Id}", cookie))
        {
            var html = WebUtility.HtmlDecode(await detail.Content.ReadAsStringAsync());
            Assert.Contains($"深层评论 8 {suffix}", html, StringComparison.Ordinal);
        }

        var shortBody = $"草稿{suffix}";
        var failBody = shortBody[..1];
        using (var failed = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/posts/{post.Id}/comments",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = form.Token,
                       ["bodyMd"] = failBody
                   },
                   cookie,
                   form.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, failed.StatusCode);
            Assert.Contains("error=", failed.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        using (var restored = await GetWithCookieAsync(client, $"/posts/{post.Id}", cookie))
        {
            var html = WebUtility.HtmlDecode(await restored.Content.ReadAsStringAsync());
            Assert.Matches(
                $"<textarea[^>]*id=\"comment-body\"[^>]*>\\s*{failBody}\\s*</textarea>",
                html);
            Assert.Contains("已恢复上次未成功提交的评论草稿", html, StringComparison.Ordinal);
        }
    }

    [DatabaseFact]
    public async Task Invalid_reply_target_is_reported_instead_of_silently_becoming_root()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var author = ServiceTestSupport.NewUser("invalid-author");
        var member = ServiceTestSupport.NewUser("invalid-member");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id, title: $"无效回复目标 {suffix}");
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.AddRange(author, member);
            db.Categories.Add(category);
            db.Posts.Add(post);
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(member.Handle);
        using var response = await GetWithCookieAsync(
            client,
            $"/posts/{post.Id}?replyTo={Guid.NewGuid()}",
            cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("要回复的评论不存在", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"parentId\"", html, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(
        HttpClient client,
        string path,
        string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }
}