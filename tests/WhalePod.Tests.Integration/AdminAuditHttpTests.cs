using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class AdminAuditHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Csv_export_is_admin_only_bounded_and_redacted()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("audit-http-admin", UserRoles.Admin);
        admin.Handle = $"audit-http-admin-{suffix}";
        var actionPrefix = $"audit-http-{suffix}.export";
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(admin);
        db.AuditLogs.AddRange(
            NewAudit(admin.Id, actionPrefix, new { order = 1, token = "first-secret" }, now.AddMinutes(-2)),
            NewAudit(admin.Id, actionPrefix, new { order = 2, email = "secret@example.test", visible = "visible-field" }, now.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var session = await environment.LoginAsync(admin.Handle);
        using var client = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        var form = await LoadFormAsync(client, "/admin/audit", session);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);

        using var export = await PostFormAsync(
            client,
            "/admin/audit/export",
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = form.Token,
                ["from"] = today.AddDays(-1).ToString("yyyy-MM-dd"),
                ["to"] = today.ToString("yyyy-MM-dd"),
                ["actor"] = string.Empty,
                ["action"] = actionPrefix,
                ["targetType"] = string.Empty,
                ["targetId"] = string.Empty
            },
            session,
            form.CsrfCookie);

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", export.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains(
            "attachment",
            export.Content.Headers.ContentDisposition?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        var csvBytes = await export.Content.ReadAsByteArrayAsync();
        Assert.True(csvBytes.Length >= 3);
        Assert.Equal(0xEF, csvBytes[0]);
        Assert.Equal(0xBB, csvBytes[1]);
        Assert.Equal(0xBF, csvBytes[2]);
        var csv = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains(actionPrefix, csv, StringComparison.Ordinal);
        Assert.Contains("visible-field", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("first-secret", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.test", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113", csv, StringComparison.Ordinal);

        db.ChangeTracker.Clear();
        var exportAudit = await db.AuditLogs.SingleAsync(log =>
            log.ActorId == admin.Id && log.Action == "audit.exported");
        Assert.Equal(2, exportAudit.MetadataJson.RootElement.GetProperty("rows").GetInt32());

        using var tooWide = await PostFormAsync(
            client,
            "/admin/audit/export",
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = form.Token,
                ["from"] = today.AddDays(-32).ToString("yyyy-MM-dd"),
                ["to"] = today.ToString("yyyy-MM-dd"),
                ["actor"] = string.Empty,
                ["action"] = actionPrefix,
                ["targetType"] = string.Empty,
                ["targetId"] = string.Empty
            },
            session,
            form.CsrfCookie);
        Assert.Equal(HttpStatusCode.Redirect, tooWide.StatusCode);
        Assert.Equal(
            "/admin/audit?error=range-too-large",
            tooWide.Headers.Location?.ToString());
    }

    private static AuditLog NewAudit(Guid actorId, string action, object metadata, DateTimeOffset createdAt)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = actorId,
            Action = action,
            TargetType = "user",
            TargetId = actorId,
            RequestId = "test-request",
            IpHash = new byte[32],
            MetadataJson = JsonDocument.Parse(JsonSerializer.Serialize(metadata)),
            CreatedAt = createdAt
        };
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
        return new FormContext(WebUtility.HtmlDecode(tokenMatch.Groups[1].Value), csrf!);
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