using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class ActivationPageHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Activation_page_serves_static_assets_but_keeps_dynamic_pages_gated()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var user = ServiceTestSupport.NewUser("activation-assets", activated: false);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var sessionCookie = await environment.LoginAsync(user.Handle);
        using var client = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

        using var activateRequest = new HttpRequestMessage(HttpMethod.Get, "/activate");
        activateRequest.Headers.Add("Cookie", sessionCookie);
        using var activate = await client.SendAsync(activateRequest);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        var html = await activate.Content.ReadAsStringAsync();
        var assetUrls = Regex.Matches(html, "(?:href|src)=\"([^\"]+\\.(?:css|js))\"")
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .Select(url => url.StartsWith('/') ? url : $"/{url}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains(assetUrls, url => url.Contains("app.", StringComparison.Ordinal) && url.EndsWith(".css"));
        Assert.Contains(assetUrls, url => url.Contains("WhalePod.Web.", StringComparison.Ordinal) && url.EndsWith(".styles.css"));
        Assert.Contains(assetUrls, url => url.EndsWith(".razor.js", StringComparison.Ordinal));

        foreach (var url in assetUrls)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", sessionCookie);
            using var response = await client.SendAsync(request);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{url} returned {(int)response.StatusCode} instead of 200.");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (url.EndsWith(".css", StringComparison.Ordinal))
            {
                Assert.Equal("text/css", mediaType);
            }
            else
            {
                Assert.Contains("javascript", mediaType);
            }
        }

        using (var editorRequest = new HttpRequestMessage(HttpMethod.Get, "/editor"))
        {
            editorRequest.Headers.Add("Cookie", sessionCookie);
            using var editor = await client.SendAsync(editorRequest);
            Assert.Equal(HttpStatusCode.Redirect, editor.StatusCode);
            Assert.Equal("/activate", editor.Headers.Location?.OriginalString);
        }

        db.ChangeTracker.Clear();
        var stored = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        stored.ActivatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        using (var editorRequest = new HttpRequestMessage(HttpMethod.Get, "/editor"))
        {
            editorRequest.Headers.Add("Cookie", sessionCookie);
            using var editor = await client.SendAsync(editorRequest);
            Assert.Equal(HttpStatusCode.OK, editor.StatusCode);
        }
    }
}