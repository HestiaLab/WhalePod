using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class AccountHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Account_deletion_form_requires_antiforgery_and_signs_out()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var user = ServiceTestSupport.NewUser("http-account-delete");
        user.Handle = $"http-account-delete-{suffix}";
        user.Email = $"account-delete-{suffix}@example.test";
        db.Users.Add(user);
        await db.SaveChangesAsync();

        using var client = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        var sessionCookie = await environment.LoginAsync(user.Handle);
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, "/settings/account");
        pageRequest.Headers.Add("Cookie", sessionCookie);
        using var page = await client.SendAsync(pageRequest);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, "Antiforgery token was not rendered.");
        var token = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value);
        var csrfCookie = page.Headers
            .TryGetValues("Set-Cookie", out var setCookies)
            ? setCookies.Select(value => value.Split(';', 2)[0])
                .Single(value => value.StartsWith("whalepod_csrf=", StringComparison.Ordinal))
            : throw new InvalidOperationException("Antiforgery cookie was not set.");

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/settings/account/delete")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["confirmHandle"] = user.Handle,
                ["confirmPermanent"] = "true"
            })
        };
        crossOrigin.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        crossOrigin.Headers.Add("Origin", "https://evil.test");
        using (var rejected = await client.SendAsync(crossOrigin))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Post, "/settings/account/delete")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["confirmHandle"] = user.Handle,
                ["confirmPermanent"] = "true"
            })
        };
        deleteRequest.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        using (var response = await client.SendAsync(deleteRequest))
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/account/deleted", response.Headers.Location?.ToString());
        }

        db.ChangeTracker.Clear();
        var stored = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
        Assert.Equal(UserStatuses.Deleted, stored.Status);
        Assert.Null(stored.Email);
        Assert.StartsWith("deleted-", stored.Handle, StringComparison.Ordinal);
        Assert.True(await db.EmailOutbox.AnyAsync(candidate =>
            candidate.UserId == user.Id && candidate.Template == "account_deleted"));
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "account.deleted" && log.TargetId == user.Id));
    }
}