using System.Net;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Tests.Integration;

namespace WhalePod.Tests.EndToEnd;

[CollectionDefinition("e2e-http")]
public sealed class E2eHttpCollection : ICollectionFixture<ApiHttpEnvironment>;

[Collection("e2e-http")]
public sealed class PublicFlowTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Activation_editor_moderation_comment_and_public_visibility_flow()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("e2e-member");
        member.Handle = $"e2e-member-{suffix}";
        var admin = ServiceTestSupport.NewUser("e2e-admin", UserRoles.Admin);
        admin.Handle = $"e2e-admin-{suffix}";
        var pending = ServiceTestSupport.NewUser("e2e-pending", activated: false);
        pending.Handle = $"e2e-pending-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(member, admin, pending);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        using var web = WebFormTestSupport.CreateClient(environment.WebFactory);
        var pendingCookie = await environment.LoginAsync(pending.Handle);
        using (var activate = await GetWithCookieAsync(web, "/activate", pendingCookie))
        {
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
            var html = await activate.Content.ReadAsStringAsync();
            var stylesheet = Regex.Match(html, "href=\"(?<path>[^\"]+\\.css)\"");
            Assert.True(stylesheet.Success, "The activation page does not reference a stylesheet.");
            using var css = await GetWithCookieAsync(web, stylesheet.Groups["path"].Value, pendingCookie);
            Assert.Equal(HttpStatusCode.OK, css.StatusCode);
            Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        }

        var memberCookie = await environment.LoginAsync(member.Handle);
        var memberForm = await WebFormTestSupport.LoadFormAsync(web, "/activate", memberCookie);
        var title = $"端到端流程标题 {suffix}";
        using (var create = await PostFormAsync(
                   web,
                   "/editor/create",
                   memberForm,
                   memberCookie,
                   new Dictionary<string, string>
                   {
                       ["title"] = title,
                       ["categoryId"] = category.Id.ToString(),
                       ["bodyMd"] = "这是端到端流程测试正文，长度足够并且需要人工审核。"
                   }))
        {
            Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
            Assert.Contains("/posts/", create.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        db.ChangeTracker.Clear();
        var createdPost = await db.Posts.AsNoTracking()
            .SingleAsync(post => post.Title == title);
        Assert.Equal(ModerationStatuses.Pending, createdPost.ModerationStatus);

        var adminCookie = await environment.LoginAsync(admin.Handle);
        var adminForm = await WebFormTestSupport.LoadFormAsync(web, "/activate", adminCookie);
        using (var approve = await PostFormAsync(
                   web,
                   $"/admin/moderation/post/{createdPost.Id}/action",
                   adminForm,
                   adminCookie,
                   new Dictionary<string, string>
                   {
                       ["action"] = ModerationActions.Approve,
                       ["expectedContentVersion"] = createdPost.Version.ToString(),
                       ["confirm"] = "yes"
                   }))
        {
            Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
        }

        db.ChangeTracker.Clear();
        Assert.Equal(
            ModerationStatuses.Approved,
            (await db.Posts.AsNoTracking().SingleAsync(post => post.Id == createdPost.Id)).ModerationStatus);

        using var anonymous = WebFormTestSupport.CreateClient(environment.WebFactory);
        using (var publicPost = await anonymous.GetAsync($"/posts/{createdPost.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, publicPost.StatusCode);
            var html = WebUtility.HtmlDecode(await publicPost.Content.ReadAsStringAsync());
            Assert.Contains(title, html, StringComparison.Ordinal);
        }

        var commentBody = $"端到端评论内容 {suffix}，长度足够。";
        using (var comment = await PostFormAsync(
                   web,
                   $"/posts/{createdPost.Id}/comments",
                   memberForm,
                   memberCookie,
                   new Dictionary<string, string>
                   {
                       ["bodyMd"] = commentBody
                   }))
        {
            Assert.Equal(HttpStatusCode.Redirect, comment.StatusCode);
        }

        db.ChangeTracker.Clear();
        var createdComment = await db.Comments.AsNoTracking()
            .SingleAsync(candidate => candidate.BodyMd == commentBody);
        Assert.Equal(ModerationStatuses.Pending, createdComment.ModerationStatus);

        using (var approveComment = await PostFormAsync(
                   web,
                   $"/admin/moderation/comment/{createdComment.Id}/action",
                   adminForm,
                   adminCookie,
                   new Dictionary<string, string>
                   {
                       ["action"] = ModerationActions.Approve,
                       ["expectedContentVersion"] = createdComment.Version.ToString(),
                       ["confirm"] = "yes"
                   }))
        {
            Assert.Equal(HttpStatusCode.Redirect, approveComment.StatusCode);
        }

        using (var publicPost = await anonymous.GetAsync($"/posts/{createdPost.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, publicPost.StatusCode);
            var html = WebUtility.HtmlDecode(await publicPost.Content.ReadAsStringAsync());
            Assert.Contains(commentBody, html, StringComparison.Ordinal);
        }

        using var api = environment.ApiFactory.CreateClient();
        using (var apiPost = await api.GetAsync($"/api/v1/posts/{createdPost.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, apiPost.StatusCode);
            using var document = System.Text.Json.JsonDocument.Parse(
                await apiPost.Content.ReadAsStringAsync());
            Assert.Equal(
                title,
                document.RootElement.GetProperty("data").GetProperty("title").GetString());
        }
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(
        HttpClient client,
        string path,
        string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", sessionCookie);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string path,
        WebFormTestSupport.FormContext form,
        string sessionCookie,
        Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = form.Token;
        return WebFormTestSupport.PostFormAsync(client, path, values, sessionCookie, form.CsrfCookie);
    }
}