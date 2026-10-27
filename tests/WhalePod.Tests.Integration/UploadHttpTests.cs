using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class UploadHttpTests(ApiHttpEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [DatabaseFact]
    public async Task Upload_requires_csrf_rejects_bad_files_and_wires_into_post_images()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("http-upload-member");
        member.Handle = $"http-upload-member-{suffix}";
        var pending = ServiceTestSupport.NewUser("http-upload-pending", activated: false);
        pending.Handle = $"http-upload-pending-{suffix}";
        var category = ServiceTestSupport.NewCategory();
        db.Users.AddRange(member, pending);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using (var anonymous = await UploadAsync(
                   client,
                   TestImages.Png(),
                   "anonymous.png",
                   "image/png",
                   null,
                   null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("AUTH_REQUIRED", await ReadCodeAsync(anonymous));
        }

        var memberSession = await environment.LoginAsync(member.Handle);
        var (memberCsrf, memberAntiforgery) = await environment.ApiFactory.GetCsrfAsync(memberSession);

        using (var missingCsrf = await UploadAsync(
                   client,
                   TestImages.Png(),
                   "missing-csrf.png",
                   "image/png",
                   memberSession,
                   null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(missingCsrf));
        }

        Assert.Equal(0, await db.MediaUploads.CountAsync(upload => upload.OwnerId == member.Id));
        Assert.Empty(Directory.EnumerateFiles(environment.MediaRootPath, "*", SearchOption.AllDirectories));

        using (var unsupported = await UploadAsync(
                   client,
                   TestImages.Svg(),
                   "payload.svg",
                   "image/svg+xml",
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
            Assert.Equal("UPLOAD_UNSUPPORTED_TYPE", await ReadCodeAsync(unsupported));
        }

        var oversized = new byte[MediaLimits.MaxImageBytes + 1];
        using (var tooLarge = await UploadAsync(
                   client,
                   oversized,
                   "too-large.png",
                   "image/png",
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
            Assert.Equal("UPLOAD_TOO_LARGE", await ReadCodeAsync(tooLarge));
        }

        Assert.Equal(0, await db.MediaUploads.CountAsync(upload => upload.OwnerId == member.Id));

        Guid uploadId;
        string url;
        using (var uploaded = await UploadAsync(
                   client,
                   TestImages.Png(30, 18),
                   "ignored-extension.svg",
                   "text/plain",
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            var body = await uploaded.Content.ReadAsStringAsync();
            Assert.True(uploaded.StatusCode == HttpStatusCode.Created, body);
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            uploadId = data.GetProperty("id").GetGuid();
            url = data.GetProperty("url").GetString()!;
            Assert.Equal("image/webp", data.GetProperty("mimeType").GetString());
            Assert.Equal(30, data.GetProperty("width").GetInt32());
            Assert.Equal(18, data.GetProperty("height").GetInt32());
            Assert.True(data.GetProperty("byteSize").GetInt64() > 0);
            Assert.Matches(@"^/media/\d{4}/\d{2}/[0-9a-f]{32}\.webp$", url);
        }

        var mediaPath = Path.Combine(
            environment.MediaRootPath,
            url["/media/".Length..].Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(mediaPath));

        db.ChangeTracker.Clear();
        var upload = await db.MediaUploads.SingleAsync(candidate => candidate.Id == uploadId);
        Assert.Equal(MediaUploadStatuses.Temp, upload.Status);
        Assert.Equal(member.Id, upload.OwnerId);
        Assert.Equal(url, upload.Url);

        using (var media = await GetAsync(client, url, null))
        {
            Assert.Equal(HttpStatusCode.OK, media.StatusCode);
            Assert.Equal("image/webp", media.Content.Headers.ContentType?.MediaType);
            Assert.Contains("immutable", media.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Contains("nosniff", media.Headers.GetValues("X-Content-Type-Options"));
        }

        using (var webClient = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        }))
        using (var webMedia = await webClient.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, webMedia.StatusCode);
            Assert.Equal("image/webp", webMedia.Content.Headers.ContentType?.MediaType);
        }

        using (var created = await PostJsonAsync(
                   client,
                   "/api/v1/posts",
                   new
                   {
                       title = $"带上传图片的 HTTP 帖子 {suffix}",
                       categoryId = category.Id,
                       bodyMd = "这是引用上传图片的测试正文，长度足够。",
                       images = new[] { new { id = uploadId, altText = "HTTP 上传图片" } }
                   },
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            var body = await created.Content.ReadAsStringAsync();
            Assert.True(created.StatusCode == HttpStatusCode.Created, body);
            using var document = JsonDocument.Parse(body);
            var image = Assert.Single(
                document.RootElement.GetProperty("data").GetProperty("images").EnumerateArray());
            Assert.Equal(url, image.GetProperty("url").GetString());
            Assert.Equal("HTTP 上传图片", image.GetProperty("altText").GetString());
            Assert.Equal("image/webp", image.GetProperty("mimeType").GetString());
        }

        db.ChangeTracker.Clear();
        Assert.Equal(
            MediaUploadStatuses.Attached,
            (await db.MediaUploads.SingleAsync(candidate => candidate.Id == uploadId)).Status);

        var pendingSession = await environment.LoginAsync(pending.Handle);
        var (pendingCsrf, pendingAntiforgery) = await environment.ApiFactory.GetCsrfAsync(pendingSession);
        using (var pendingUpload = await UploadAsync(
                   client,
                   TestImages.Png(),
                   "pending.png",
                   "image/png",
                   pendingSession,
                   pendingCsrf,
                   pendingAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, pendingUpload.StatusCode);
            using var document = JsonDocument.Parse(await pendingUpload.Content.ReadAsStringAsync());
            Assert.Equal(
                "activation_required",
                document.RootElement.GetProperty("details").GetProperty("reason").GetString());
        }
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        byte[] content,
        string fileName,
        string contentType,
        string? sessionCookie,
        string? csrfToken,
        string? antiforgeryCookie = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads/images")
        {
            Content = form
        };
        AddCookies(request, sessionCookie, antiforgeryCookie);
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrfToken);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        object body,
        string? sessionCookie,
        string? csrfToken,
        string? antiforgeryCookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        AddCookies(request, sessionCookie, antiforgeryCookie);
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrfToken);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string? sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddCookies(request, sessionCookie, null);
        return await client.SendAsync(request);
    }

    private static void AddCookies(
        HttpRequestMessage request,
        string? sessionCookie,
        string? antiforgeryCookie)
    {
        if (sessionCookie is null)
        {
            return;
        }

        request.Headers.Add(
            "Cookie",
            antiforgeryCookie is null
                ? sessionCookie
                : $"{sessionCookie}; {antiforgeryCookie}");
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return document.RootElement.GetProperty("code").GetString();
    }
}