using System.Net;
using System.Net.Http.Headers;

using Microsoft.EntityFrameworkCore;

using WhalePod.Tests.Integration;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class AvatarHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Avatar_upload_clear_and_invalid_type_are_handled_by_profile_form()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var member = ServiceTestSupport.NewUser("avatar-http");
        member.Handle = $"avatar-http-{suffix}";
        member.GitHubAvatarUrl = "https://avatars.githubusercontent.com/u/99?v=4";
        member.AvatarUrl = member.GitHubAvatarUrl;
        db.Users.Add(member);
        await db.SaveChangesAsync();

        using var client = WebFormTestSupport.CreateClient(environment.WebFactory);
        var session = await environment.LoginAsync(member.Handle);
        var form = await WebFormTestSupport.LoadFormAsync(client, "/settings/profile", session);

        using (var upload = await PostAvatarAsync(
                   client,
                   "/settings/profile/avatar",
                   form,
                   session,
                   TestImages.Png(32, 32),
                   "avatar.png",
                   "image/png"))
        {
            Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
            Assert.Contains("message=avatar-updated", upload.Headers.Location?.OriginalString);
        }

        db.ChangeTracker.Clear();
        var uploaded = await db.Users.SingleAsync(user => user.Id == member.Id);
        Assert.StartsWith("/media/", uploaded.AvatarUrl, StringComparison.Ordinal);
        Assert.True(File.Exists(ResolveMediaPath(uploaded.AvatarUrl!)));
        Assert.Equal(
            1,
            await db.MediaUploads.CountAsync(media =>
                media.OwnerId == member.Id && media.Status == "avatar"));

        using (var invalid = await PostAvatarAsync(
                   client,
                   "/settings/profile/avatar",
                   form,
                   session,
                   TestImages.Svg(),
                   "avatar.svg",
                   "image/svg+xml"))
        {
            Assert.Equal(HttpStatusCode.Redirect, invalid.StatusCode);
            Assert.Contains("error=avatar-type", invalid.Headers.Location?.OriginalString);
        }

        using (var clear = await WebFormTestSupport.PostFormAsync(
                   client,
                   "/settings/profile/avatar/clear",
                   new Dictionary<string, string>
                   {
                       ["__RequestVerificationToken"] = form.Token
                   },
                   session,
                   form.CsrfCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, clear.StatusCode);
            Assert.Contains("message=avatar-cleared", clear.Headers.Location?.OriginalString);
        }

        db.ChangeTracker.Clear();
        var cleared = await db.Users.SingleAsync(user => user.Id == member.Id);
        Assert.Equal(member.GitHubAvatarUrl, cleared.AvatarUrl);
        Assert.Equal(
            0,
            await db.MediaUploads.CountAsync(media =>
                media.OwnerId == member.Id && media.Status == "avatar"));
    }

    private static async Task<HttpResponseMessage> PostAvatarAsync(
        HttpClient client,
        string path,
        WebFormTestSupport.FormContext form,
        string session,
        byte[] bytes,
        string fileName,
        string contentType)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(form.Token), "__RequestVerificationToken");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "avatar", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content
        };
        request.Headers.Add("Cookie", $"{session}; {form.CsrfCookie}");
        return await client.SendAsync(request);
    }

    private string ResolveMediaPath(string url)
    {
        Assert.StartsWith("/media/", url, StringComparison.Ordinal);
        return Path.Combine(
            environment.MediaRootPath,
            url["/media/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }
}