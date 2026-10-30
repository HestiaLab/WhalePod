using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class PublicPageHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Public_pages_and_discovery_only_expose_approved_content()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var author = ServiceTestSupport.NewUser("public-ssr-author");
        author.Handle = $"public-ssr-author-{suffix}";
        author.DisplayName = $"公开作者 {suffix}";
        var category = ServiceTestSupport.NewCategory($"公开板块{suffix}");
        var tag = ServiceTestSupport.NewTag();
        tag.Name = $"公开标签{suffix}";
        var approved = ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            title: $"公开中文实测标题 {suffix}",
            body: $"这是公开正文，包含可复现步骤与中文检索词 {suffix}。");
        approved.EnvKernel = "6.8.0";
        approved.EnvPlatform = "linux-x64";
        approved.EnvModel = "deepseek-flash";
        var artifact = new Artifact
        {
            PostId = approved.Id,
            Kind = "shell",
            Name = "复现脚本",
            Content = "echo whalepod",
            SortOrder = 0
        };
        var rejected = ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            ModerationStatuses.Rejected,
            title: $"驳回隐藏标题 {suffix}");
        var deleted = ServiceTestSupport.NewPost(
            author.Id,
            category.Id,
            title: $"删除隐藏标题 {suffix}");
        deleted.DeletedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var approvedComment = ServiceTestSupport.NewComment(
            approved.Id,
            author.Id,
            body: $"公开评论内容 {suffix}");
        var pendingComment = ServiceTestSupport.NewComment(
            approved.Id,
            author.Id,
            ModerationStatuses.Pending,
            body: $"待审评论内容 {suffix}");
        db.Users.Add(author);
        db.Categories.Add(category);
        db.Tags.Add(tag);
        db.Posts.AddRange(approved, rejected, deleted);
        db.Artifacts.Add(artifact);
        db.Comments.AddRange(approvedComment, pendingComment);
        db.PostTags.Add(new PostTag { PostId = approved.Id, TagId = tag.Id });
        await db.SaveChangesAsync();

        using var client = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

        var home = await GetTextAsync(client, "/");
        Assert.Contains(approved.Title, home, StringComparison.Ordinal);
        Assert.DoesNotContain(rejected.Title, home, StringComparison.Ordinal);
        Assert.DoesNotContain(deleted.Title, home, StringComparison.Ordinal);
        Assert.Contains("property=\"og:title\"", home, StringComparison.Ordinal);
        Assert.Contains("rel=\"canonical\"", home, StringComparison.Ordinal);
        Assert.Contains("application/ld+json", home, StringComparison.Ordinal);
        Assert.Contains("linux-x64", home, StringComparison.Ordinal);
        Assert.Contains("复制首份产物", home, StringComparison.Ordinal);
        Assert.Contains($"#artifact-{artifact.Id}", home, StringComparison.Ordinal);

        var detail = await GetTextAsync(client, $"/posts/{approved.Id}");
        Assert.Contains(approved.Title, detail, StringComparison.Ordinal);
        Assert.Contains(approvedComment.BodyMd, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(pendingComment.BodyMd, detail, StringComparison.Ordinal);
        Assert.Contains("DiscussionForumPosting", detail, StringComparison.Ordinal);
        Assert.Contains($"canonical\" href=\"http://localhost:5224/posts/{approved.Id}", detail, StringComparison.Ordinal);
        Assert.Contains("复制环境信息", detail, StringComparison.Ordinal);
        Assert.Contains("先审查内容再执行", detail, StringComparison.Ordinal);
        Assert.Contains($"data-copy-artifact-id=\"{artifact.Id}\"", detail, StringComparison.Ordinal);

        await GetTextAsync(client, $"/posts/{approved.Id}");

        db.ChangeTracker.Clear();
        var viewCount = await db.Posts
            .AsNoTracking()
            .Where(post => post.Id == approved.Id)
            .Select(post => post.ViewCount)
            .SingleAsync();
        Assert.Equal(1, viewCount);

        using (var rejectedResponse = await client.GetAsync($"/posts/{rejected.Id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, rejectedResponse.StatusCode);
            Assert.Contains("内容不存在", await rejectedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var tags = await GetTextAsync(client, "/tags");
        Assert.Contains(tag.Name, tags, StringComparison.Ordinal);
        var tagDetail = await GetTextAsync(client, $"/tags/{tag.Slug}");
        Assert.Contains(approved.Title, tagDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(rejected.Title, tagDetail, StringComparison.Ordinal);

        var search = await GetTextAsync(client, $"/search?q={Uri.EscapeDataString("公开中文实测")}");
        Assert.Contains(approved.Title, search, StringComparison.Ordinal);
        Assert.DoesNotContain(rejected.Title, search, StringComparison.Ordinal);

        var profile = await GetTextAsync(client, $"/users/{author.Handle}");
        Assert.Contains(author.DisplayName, profile, StringComparison.Ordinal);
        Assert.Contains(approved.Title, profile, StringComparison.Ordinal);

        string? rssETag;
        using (var rssResponse = await client.GetAsync("/rss.xml"))
        {
            Assert.Equal(HttpStatusCode.OK, rssResponse.StatusCode);
            Assert.Equal("application/rss+xml", rssResponse.Content.Headers.ContentType?.MediaType);
            rssETag = rssResponse.Headers.ETag?.Tag;
            Assert.False(string.IsNullOrWhiteSpace(rssETag));
            var rss = await rssResponse.Content.ReadAsStringAsync();
            Assert.Contains("<rss", rss, StringComparison.Ordinal);
            Assert.Contains(approved.Title, rss, StringComparison.Ordinal);
            Assert.DoesNotContain(rejected.Title, rss, StringComparison.Ordinal);
        }

        using (var conditional = new HttpRequestMessage(HttpMethod.Get, "/rss.xml"))
        {
            conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(rssETag!));
            using var notModified = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        }

        using (var sitemapResponse = await client.GetAsync("/sitemap.xml"))
        {
            Assert.Equal(HttpStatusCode.OK, sitemapResponse.StatusCode);
            Assert.Equal("application/xml", sitemapResponse.Content.Headers.ContentType?.MediaType);
            var index = await sitemapResponse.Content.ReadAsStringAsync();
            Assert.Contains("<sitemapindex", index, StringComparison.Ordinal);
            Assert.Contains("/sitemap-posts-1.xml", index, StringComparison.Ordinal);
            Assert.Contains("/sitemap-pages.xml", index, StringComparison.Ordinal);
        }

        using (var postsResponse = await client.GetAsync("/sitemap-posts-1.xml"))
        {
            Assert.Equal(HttpStatusCode.OK, postsResponse.StatusCode);
            var postsSitemap = await postsResponse.Content.ReadAsStringAsync();
            Assert.Contains($"/posts/{approved.Id}", postsSitemap, StringComparison.Ordinal);
            Assert.DoesNotContain($"/posts/{rejected.Id}", postsSitemap, StringComparison.Ordinal);
        }

        using (var pagesResponse = await client.GetAsync("/sitemap-pages.xml"))
        {
            Assert.Equal(HttpStatusCode.OK, pagesResponse.StatusCode);
            var pagesSitemap = await pagesResponse.Content.ReadAsStringAsync();
            Assert.Contains($"/users/{author.Handle}", pagesSitemap, StringComparison.Ordinal);
            Assert.Contains($"/tags/{tag.Slug}", pagesSitemap, StringComparison.Ordinal);
            Assert.Contains($"/?category={category.Slug}", pagesSitemap, StringComparison.Ordinal);
        }

        var robots = await GetTextAsync(client, "/robots.txt");
        Assert.Contains("Sitemap:", robots, StringComparison.Ordinal);
        Assert.Contains("Disallow: /admin/", robots, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Public_interaction_forms_create_pending_content_and_hide_it_from_others()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var author = ServiceTestSupport.NewUser("public-form-author");
        author.Handle = $"public-form-author-{suffix}";
        var member = ServiceTestSupport.NewUser("public-form-member");
        member.Handle = $"public-form-member-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(author.Id, category.Id);
        db.Users.AddRange(author, member);
        db.Categories.Add(category);
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        using var client = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        var session = await environment.LoginAsync(member.Handle);
        var commentForm = await LoadFormAsync(client, $"/posts/{post.Id}", session);
        using (var commentResponse = await PostFormAsync(
                   client,
                   $"/posts/{post.Id}/comments",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = commentForm.Token,
                       ["bodyMd"] = $"这是通过公开 SSR 表单提交的评论 {suffix}，等待审核。"
                   },
                   session,
                   commentForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, commentResponse.StatusCode);
            Assert.StartsWith($"/posts/{post.Id}?message=pending", commentResponse.Headers.Location?.ToString(), StringComparison.Ordinal);
        }

        db.ChangeTracker.Clear();
        var comment = await db.Comments.SingleAsync(candidate =>
            candidate.AuthorId == member.Id && candidate.PostId == post.Id);
        Assert.Equal(ModerationStatuses.Pending, comment.ModerationStatus);

        var editorForm = await LoadFormAsync(client, "/editor", session);
        using (var previewResponse = await PostFormAsync(
                   client,
                   "/editor/preview",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = editorForm.Token,
                       ["bodyMd"] = "**粗体**与<script>alert('x')</script>"
                   },
                   session,
                   editorForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            Assert.Equal("text/html", previewResponse.Content.Headers.ContentType?.MediaType);
            var preview = await previewResponse.Content.ReadAsStringAsync();
            Assert.Contains("<strong>粗体</strong>", preview, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", preview, StringComparison.OrdinalIgnoreCase);
        }

        var title = $"公开编辑器提交标题 {suffix}";
        using (var editorResponse = await PostFormAsync(
                   client,
                   "/editor/create",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = editorForm.Token,
                       ["title"] = title,
                       ["categoryId"] = category.Id.ToString(),
                       ["bodyMd"] = $"这是通过公开 SSR 编辑器提交的正文 {suffix}，长度足够。"
                   },
                   session,
                   editorForm.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, editorResponse.StatusCode);
            var location = editorResponse.Headers.Location?.ToString();
            Assert.NotNull(location);
            Assert.StartsWith("/posts/", location, StringComparison.Ordinal);
            Assert.EndsWith("?message=post-pending", location, StringComparison.Ordinal);
        }

        db.ChangeTracker.Clear();
        var created = await db.Posts.SingleAsync(candidate =>
            candidate.AuthorId == member.Id && candidate.Title == title);
        Assert.Equal(ModerationStatuses.Pending, created.ModerationStatus);

        var authorView = await GetTextAsync(client, $"/posts/{created.Id}", session);
        Assert.Contains(created.Title, authorView, StringComparison.Ordinal);
        Assert.Contains("当前内容状态：待审核", authorView, StringComparison.Ordinal);

        using var anonymous = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        using var anonymousResponse = await anonymous.GetAsync($"/posts/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, anonymousResponse.StatusCode);
    }

    [DatabaseFact]
    public async Task Search_page_enforces_public_rate_limit()
    {
        using var limitedFactory = new WhalePodWebFactory(
            environment.KeysDirectory,
            new Dictionary<string, string?>
            {
                ["PublicSearch:RateLimitPerMinute"] = "1"
            });
        using var client = limitedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

        using (var first = await client.GetAsync("/search?q=test"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var second = await client.GetAsync("/search?q=test"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task Unknown_route_returns_html_404()
    {
        using var client = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

        using var response = await client.GetAsync($"/no-such-page-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("内容不存在", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Category_tabs_are_capped_and_category_index_lists_all()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        string lastSlug;
        await using (var db = ServiceTestSupport.CreateContext())
        {
            await db.Database.MigrateAsync();
            for (var index = 0; index < 35; index++)
            {
                var category = ServiceTestSupport.NewCategory($"板块上限{index}-{suffix}");
                category.DisplayOrder = -100_000 + index;
                db.Categories.Add(category);
            }

            await db.SaveChangesAsync();
            lastSlug = await db.Categories
                .Where(category => category.Name == $"板块上限34-{suffix}")
                .Select(category => category.Slug)
                .SingleAsync();
        }

        using var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        using (var home = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            var html = WebUtility.HtmlDecode(await home.Content.ReadAsStringAsync());
            Assert.Contains($"板块上限0-{suffix}", html, StringComparison.Ordinal);
            Assert.Contains("更多板块", html, StringComparison.Ordinal);
            Assert.DoesNotContain($"板块上限34-{suffix}", html, StringComparison.Ordinal);
        }

        using (var selected = await client.GetAsync(
                   $"/?category={Uri.EscapeDataString(lastSlug)}"))
        {
            Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
            var html = WebUtility.HtmlDecode(await selected.Content.ReadAsStringAsync());
            Assert.Contains($"板块上限34-{suffix}", html, StringComparison.Ordinal);
        }

        using (var indexPage = await client.GetAsync("/categories"))
        {
            Assert.Equal(HttpStatusCode.OK, indexPage.StatusCode);
            var html = WebUtility.HtmlDecode(await indexPage.Content.ReadAsStringAsync());
            Assert.Contains($"板块上限0-{suffix}", html, StringComparison.Ordinal);
            Assert.Contains($"板块上限34-{suffix}", html, StringComparison.Ordinal);
        }

        using (var pagesSitemap = await client.GetAsync("/sitemap-pages.xml"))
        {
            Assert.Equal(HttpStatusCode.OK, pagesSitemap.StatusCode);
            Assert.Contains("/categories", await pagesSitemap.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    private static async Task<string> GetTextAsync(
        HttpClient client,
        string path,
        string? sessionCookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (sessionCookie is not null)
        {
            request.Headers.Add("Cookie", sessionCookie);
        }

        using var response = await client.SendAsync(request);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return body;
    }

    private static async Task<FormContext> LoadFormAsync(
        HttpClient client,
        string path,
        string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", sessionCookie);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, $"Antiforgery token missing on {path}.");
        var csrf = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(value => value.Split(';', 2)[0])
                .FirstOrDefault(value => value.StartsWith("whalepod_csrf=", StringComparison.Ordinal))
            : null;
        Assert.NotNull(csrf);
        return new FormContext(
            WebUtility.HtmlDecode(tokenMatch.Groups[1].Value),
            csrf!);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> form,
        string sessionCookie,
        string csrfCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        return await client.SendAsync(request);
    }

    private sealed record FormContext(string Token, string CsrfCookie);
}