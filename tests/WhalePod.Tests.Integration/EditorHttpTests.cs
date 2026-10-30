using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class EditorHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Web_upload_requires_csrf_stores_owned_temp_image_and_supports_remove()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("editor-upload-member");
        member.Handle = $"editor-upload-member-{suffix}";
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.Add(member);
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(member.Handle);
        var formContext = await WebFormTestSupport.LoadFormAsync(client, "/editor", cookie);

        using (var anonymous = await UploadAsync(
                   client,
                   TestImages.Png(),
                   "anonymous.png",
                   "image/png",
                   null,
                   null,
                   null,
                   alt: "匿名上传"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Contains("/login", anonymous.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        using (var missingToken = await UploadAsync(
                   client,
                   TestImages.Png(),
                   "missing-token.png",
                   "image/png",
                   cookie,
                   null,
                   formContext.CsrfCookie,
                   alt: "缺少令牌"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.Equal(0, await db.MediaUploads.CountAsync(upload => upload.OwnerId == member.Id));
        }

        using (var unsupported = await UploadAsync(
                   client,
                   TestImages.Svg(),
                   "payload.svg",
                   "image/svg+xml",
                   cookie,
                   formContext.Token,
                   formContext.CsrfCookie,
                   alt: "SVG"))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        }

        using (var longAlt = await UploadAsync(
                   client,
                   TestImages.Png(),
                   "long-alt.png",
                   "image/png",
                   cookie,
                   formContext.Token,
                   formContext.CsrfCookie,
                   alt: new string('a', 201)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, longAlt.StatusCode);
        }

        using (var oversized = await UploadAsync(
                   client,
                   new byte[MediaLimits.MaxImageBytes + 1],
                   "too-large.png",
                   "image/png",
                   cookie,
                   formContext.Token,
                   formContext.CsrfCookie,
                   alt: "过大文件"))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.Equal(0, await db.MediaUploads.CountAsync(upload => upload.OwnerId == member.Id));
        }

        Guid uploadId;
        string url;
        using (var uploaded = await UploadAsync(
                   client,
                   TestImages.Png(30, 18),
                   "editor.png",
                   "image/png",
                   cookie,
                   formContext.Token,
                   formContext.CsrfCookie,
                   alt: "上传替代文本"))
        {
            var body = await uploaded.Content.ReadAsStringAsync();
            Assert.True(uploaded.StatusCode == HttpStatusCode.Created, body);
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            uploadId = data.GetProperty("id").GetGuid();
            url = data.GetProperty("url").GetString()!;
            Assert.Equal("上传替代文本", data.GetProperty("altText").GetString());
            Assert.Equal("image/webp", data.GetProperty("mimeType").GetString());
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var upload = await db.MediaUploads.AsNoTracking().SingleAsync(candidate => candidate.Id == uploadId);
            Assert.Equal(MediaUploadStatuses.Temp, upload.Status);
            Assert.Equal(member.Id, upload.OwnerId);
            Assert.Equal(url, upload.Url);
        }

        using (var noScript = await UploadAsync(
                   client,
                   TestImages.Png(20, 12),
                   "no-script.png",
                   "image/png",
                   cookie,
                   formContext.Token,
                   formContext.CsrfCookie,
                   alt: "无脚本上传",
                   json: false))
        {
            Assert.Equal(HttpStatusCode.Redirect, noScript.StatusCode);
            Assert.Contains("message=image-uploaded", noScript.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        using (var editorPage = await GetWithCookieAsync(client, "/editor", cookie))
        {
            Assert.Equal(HttpStatusCode.OK, editorPage.StatusCode);
            var html = WebUtility.HtmlDecode(await editorPage.Content.ReadAsStringAsync());
            Assert.Contains(url, html, StringComparison.Ordinal);
            Assert.Contains("上传替代文本", html, StringComparison.Ordinal);
            Assert.Contains("无脚本上传", html, StringComparison.Ordinal);
        }

        using (var removed = await WebFormTestSupport.PostFormAsync(
                   client,
                   $"/editor/images/{uploadId}/remove",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token
                   },
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
            Assert.Contains("message=image-removed", removed.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.Equal(
                MediaUploadStatuses.Deleted,
                (await db.MediaUploads.AsNoTracking().SingleAsync(candidate => candidate.Id == uploadId)).Status);
        }

        using (var editorPage = await GetWithCookieAsync(client, "/editor", cookie))
        {
            var html = WebUtility.HtmlDecode(await editorPage.Content.ReadAsStringAsync());
            Assert.DoesNotContain(url, html, StringComparison.Ordinal);
        }
    }

    [DatabaseFact]
    public async Task Editor_create_accepts_ten_images_five_artifacts_and_six_tags_but_rejects_over_limit()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("editor-create-member");
        member.Handle = $"editor-create-member-{suffix}";
        var category = ServiceTestSupport.NewCategory($"编辑器板块{suffix}");
        var tags = Enumerable.Range(0, 7)
            .Select(index =>
            {
                var tag = ServiceTestSupport.NewTag();
                tag.Name = $"编辑器标签{index}-{suffix}";
                return tag;
            })
            .ToList();
        List<MediaUpload> uploads;
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.Add(member);
            db.Categories.Add(category);
            db.Tags.AddRange(tags);
            uploads = Enumerable.Range(0, 11).Select(index => NewTempUpload(member.Id, index)).ToList();
            db.MediaUploads.AddRange(uploads);
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(member.Handle);
        var formContext = await WebFormTestSupport.LoadFormAsync(client, "/editor", cookie);

        var title = $"编辑器提交标题 {suffix}";
        var form = BuildCreateForm(
            formContext.Token,
            title,
            category.Id,
            uploads.Take(10).Select((upload, index) =>
                (upload.Id, Alt: $"替代文本{index}")).ToList(),
            Enumerable.Range(0, 5).Select(index =>
                new ArtifactRow("shell", $"产物{index}", $"备注{index}", $"echo artifact-{index}")).ToList(),
            tags.Take(6).Select(tag => tag.Slug).ToList());

        using (var created = await WebFormTestSupport.PostFormAsync(
                   client,
                   "/editor/create",
                   form,
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            Assert.Contains("message=post-pending", created.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            var post = await db.Posts.AsNoTracking().SingleAsync(candidate =>
                candidate.AuthorId == member.Id && candidate.Title == title);
            var images = await db.PostImages
                .AsNoTracking()
                .Where(image => image.PostId == post.Id)
                .OrderBy(image => image.SortOrder)
                .ToListAsync();
            Assert.Equal(10, images.Count);
            Assert.Equal(
                Enumerable.Range(0, 10).Select(index => $"替代文本{index}").ToList(),
                images.Select(image => image.AltText ?? string.Empty).ToList());
            var artifacts = await db.Artifacts
                .AsNoTracking()
                .Where(artifact => artifact.PostId == post.Id)
                .OrderBy(artifact => artifact.SortOrder)
                .ToListAsync();
            Assert.Equal(5, artifacts.Count);
            Assert.Equal(
                Enumerable.Range(0, 5).Select(index => $"产物{index}").ToList(),
                artifacts.Select(artifact => artifact.Name).ToList());
            Assert.Equal(
                Enumerable.Range(0, 6).Select(index => tags[index].Id).ToHashSet(),
                (await db.PostTags.AsNoTracking()
                    .Where(postTag => postTag.PostId == post.Id)
                    .Select(postTag => postTag.TagId)
                    .ToListAsync()).ToHashSet());
        }

        var elevenImages = BuildCreateForm(
            formContext.Token,
            $"图片超限标题 {suffix}",
            category.Id,
            uploads.Select((upload, index) => (upload.Id, Alt: $"替代文本{index}")).ToList(),
            [],
            []);
        using (var tooManyImages = await WebFormTestSupport.PostFormAsync(
                   client, "/editor/create", elevenImages, cookie, formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, tooManyImages.StatusCode);
            Assert.Contains("field=images", tooManyImages.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        var sixArtifacts = BuildCreateForm(
            formContext.Token,
            $"产物超限标题 {suffix}",
            category.Id,
            [],
            Enumerable.Range(0, 6).Select(index =>
                new ArtifactRow("shell", $"产物{index}", string.Empty, $"echo {index}")).ToList(),
            []);
        using (var tooManyArtifacts = await WebFormTestSupport.PostFormAsync(
                   client, "/editor/create", sixArtifacts, cookie, formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, tooManyArtifacts.StatusCode);
            Assert.Contains("field=artifacts", tooManyArtifacts.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        var sevenTags = BuildCreateForm(
            formContext.Token,
            $"标签超限标题 {suffix}",
            category.Id,
            [],
            [],
            tags.Select(tag => tag.Slug).ToList());
        using (var tooManyTags = await WebFormTestSupport.PostFormAsync(
                   client, "/editor/create", sevenTags, cookie, formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, tooManyTags.StatusCode);
            Assert.Contains("field=tags", tooManyTags.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        var missingArtifactContent = BuildCreateForm(
            formContext.Token,
            $"产物缺内容标题 {suffix}",
            category.Id,
            [],
            [new ArtifactRow("shell", "缺内容的产物", string.Empty, string.Empty)],
            []);
        using (var invalidArtifact = await WebFormTestSupport.PostFormAsync(
                   client, "/editor/create", missingArtifactContent, cookie, formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, invalidArtifact.StatusCode);
            Assert.Contains("field=artifacts", invalidArtifact.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        await using (var db = ServiceTestSupport.CreateContext())
        {
            Assert.Equal(
                1,
                await db.Posts.CountAsync(candidate => candidate.AuthorId == member.Id));
        }
    }

    [DatabaseFact]
    public async Task Editor_exposes_tags_beyond_the_first_fifty()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("editor-tags-member");
        member.Handle = $"editor-tags-member-{suffix}";
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.Add(member);
            for (var index = 0; index < 60; index++)
            {
                var tag = ServiceTestSupport.NewTag();
                tag.Name = $"批量标签{index:D2}-{suffix}";
                db.Tags.Add(tag);
            }

            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(member.Handle);
        using var editorPage = await GetWithCookieAsync(client, "/editor", cookie);
        Assert.Equal(HttpStatusCode.OK, editorPage.StatusCode);
        var html = WebUtility.HtmlDecode(await editorPage.Content.ReadAsStringAsync());
        Assert.Contains($"批量标签50-{suffix}", html, StringComparison.Ordinal);
        Assert.Contains($"批量标签59-{suffix}", html, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Editor_failure_retains_fields_images_artifacts_and_tags()
    {
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("editor-retain-member");
        member.Handle = $"editor-retain-member-{suffix}";
        var category = ServiceTestSupport.NewCategory($"保留板块{suffix}");
        var tag = ServiceTestSupport.NewTag();
        tag.Name = $"保留标签{suffix}";
        await using (var db = ServiceTestSupport.CreateContext())
        {
            db.Users.Add(member);
            db.Categories.Add(category);
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
        }

        var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var cookie = await environment.LoginAsync(member.Handle);
        var formContext = await WebFormTestSupport.LoadFormAsync(client, "/editor", cookie);
        using (var uploaded = await UploadAsync(
                   client,
                   TestImages.Png(12, 9),
                   "retain.png",
                   "image/png",
                   cookie,
                   formContext.Token,
                   formContext.CsrfCookie,
                   alt: "保留替代文本"))
        {
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        var uploadId = await GetLatestUploadIdAsync(member.Id);
        var title = $"保留字段标题 {suffix}";
        var body = "这是编辑器测试正文，长度足够且包含可复现步骤。";
        var artifactName = $"保留产物{suffix}";
        var artifactContent = $"echo retained-{suffix}";
        var form = BuildCreateForm(
            formContext.Token,
            title,
            category.Id,
            [(uploadId, "保留替代文本")],
            [new ArtifactRow("shell", artifactName, "保留备注", artifactContent)],
            [tag.Slug]);
        form = form
            .Where(pair => pair.Key != "categoryId")
            .Append(new KeyValuePair<string, string>("categoryId", string.Empty))
            .ToList();

        using (var rejected = await WebFormTestSupport.PostFormAsync(
                   client, "/editor/create", form, cookie, formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
            var location = rejected.Headers.Location?.OriginalString;
            Assert.Contains("restored=1", location, StringComparison.Ordinal);
            Assert.Contains("field=categoryId", location, StringComparison.Ordinal);
        }

        using (var restored = await GetWithCookieAsync(client, "/editor", cookie))
        {
            var html = WebUtility.HtmlDecode(await restored.Content.ReadAsStringAsync());
            Assert.Contains(title, html, StringComparison.Ordinal);
            Assert.Contains(body, html, StringComparison.Ordinal);
            Assert.Contains(artifactName, html, StringComparison.Ordinal);
            Assert.Contains(artifactContent, html, StringComparison.Ordinal);
            Assert.Contains($"value=\"{tag.Slug}\" checked", html, StringComparison.Ordinal);
            Assert.Contains("保留替代文本", html, StringComparison.Ordinal);
            Assert.Contains(uploadId.ToString(), html, StringComparison.Ordinal);
        }

        using (var discarded = await WebFormTestSupport.PostFormAsync(
                   client,
                   "/editor/draft/discard",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = formContext.Token
                   },
                   cookie,
                   formContext.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, discarded.StatusCode);
            Assert.Contains("message=draft-discarded", discarded.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        using (var cleared = await GetWithCookieAsync(client, "/editor", cookie))
        {
            var html = WebUtility.HtmlDecode(await cleared.Content.ReadAsStringAsync());
            Assert.DoesNotContain(title, html, StringComparison.Ordinal);
            Assert.DoesNotContain(artifactContent, html, StringComparison.Ordinal);
        }
    }

    private static List<KeyValuePair<string, string>> BuildCreateForm(
        string token,
        string title,
        Guid categoryId,
        IReadOnlyList<(Guid Id, string Alt)> images,
        IReadOnlyList<ArtifactRow> artifacts,
        IReadOnlyList<string> tags)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("title", title),
            new("categoryId", categoryId.ToString()),
            new("bodyMd", "这是编辑器测试正文，长度足够且包含可复现步骤。"),
            new("kernel", string.Empty),
            new("platform", string.Empty),
            new("model", string.Empty)
        };
        foreach (var image in images)
        {
            form.Add(new("imageIds", image.Id.ToString()));
            form.Add(new("imageAlt", image.Alt));
        }

        foreach (var artifact in artifacts)
        {
            form.Add(new("artifactKind", artifact.Kind));
            form.Add(new("artifactName", artifact.Name));
            form.Add(new("artifactNote", artifact.Note));
            form.Add(new("artifactContent", artifact.Content));
        }

        foreach (var tag in tags)
        {
            form.Add(new("tags", tag));
        }

        return form;
    }

    private sealed record ArtifactRow(string Kind, string Name, string Note, string Content);

    private static MediaUpload NewTempUpload(Guid ownerId, int index)
    {
        var now = DateTimeOffset.UtcNow;
        return new MediaUpload
        {
            OwnerId = ownerId,
            Url = $"/media/2026/10/{Guid.NewGuid():N}.webp",
            Width = 16,
            Height = 8,
            MimeType = "image/webp",
            ByteSize = 32,
            Status = MediaUploadStatuses.Temp,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
    }

    private static async Task<Guid> GetLatestUploadIdAsync(Guid ownerId)
    {
        await using var db = ServiceTestSupport.CreateContext();
        return await db.MediaUploads
            .AsNoTracking()
            .Where(upload => upload.OwnerId == ownerId)
            .OrderByDescending(upload => upload.CreatedAt)
            .ThenByDescending(upload => upload.Id)
            .Select(upload => upload.Id)
            .FirstAsync();
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        byte[] content,
        string fileName,
        string contentType,
        string? sessionCookie,
        string? token,
        string? csrfCookie,
        string? alt,
        bool json = true)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        if (alt is not null)
        {
            form.Add(new StringContent(alt, Encoding.UTF8), "alt");
        }

        if (token is not null)
        {
            form.Add(new StringContent(token, Encoding.UTF8), "__RequestVerificationToken");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/editor/images")
        {
            Content = form
        };
        if (json)
        {
            request.Headers.Add("Accept", "application/json");
        }

        if (sessionCookie is not null)
        {
            request.Headers.Add(
                "Cookie",
                csrfCookie is null ? sessionCookie : $"{sessionCookie}; {csrfCookie}");
        }

        return await client.SendAsync(request);
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