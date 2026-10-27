using System.Net;
using System.Net.Http;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Infrastructure.Email;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class EmailHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Signed_unsubscribe_endpoint_updates_preference_without_login()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var user = ServiceTestSupport.NewUser("email-http-unsub");
        user.Email = $"unsub-http-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var tokens = new EmailUnsubscribeTokenService(
            Microsoft.Extensions.Options.Options.Create(new EmailOptions
            {
                UnsubscribeSecret = "dev-only-email-unsubscribe-secret-change-me"
            }),
            TimeProvider.System);
        var token = tokens.Create(user.Id, EmailPreferenceTypes.ModerationResults);
        using var client = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

        using (var response = await client.GetAsync(
                   $"/email/unsubscribe?token={Uri.EscapeDataString(token)}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("退订成功", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var preference = await db.EmailPreferences.SingleAsync(candidate =>
            candidate.UserId == user.Id && candidate.Type == EmailPreferenceTypes.ModerationResults);
        Assert.False(preference.Enabled);

        using (var invalid = await client.GetAsync("/email/unsubscribe?token=not-a-token"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }
}