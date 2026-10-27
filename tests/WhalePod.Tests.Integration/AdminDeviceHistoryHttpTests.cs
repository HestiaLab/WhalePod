using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class AdminDeviceHistoryHttpTests(ApiHttpEnvironment environment)
{
    [DatabaseFact]
    public async Task Device_history_page_is_admin_only_and_redacts_sensitive_data()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("device-history-http-admin", UserRoles.Admin);
        var member = ServiceTestSupport.NewUser("device-history-http-member");
        var target = ServiceTestSupport.NewUser("device-history-http-target");
        admin.Handle = $"device-history-admin-{suffix}";
        member.Handle = $"device-history-member-{suffix}";
        target.Handle = $"device-history-target-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var device = new Device
        {
            UserId = target.Id,
            PublicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            KernelVersion = "6.8.0-45-generic",
            Platform = "linux-x64",
            Label = "管理员历史页测试设备",
            Status = DeviceStatuses.Active,
            CreatedAt = now.AddDays(-2),
            LastVerifiedAt = now.AddHours(-1)
        };
        db.Users.AddRange(admin, member, target);
        db.Devices.Add(device);
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = target.Id,
            Action = "device.verification_failed",
            TargetType = "device_challenge",
            TargetId = Guid.NewGuid(),
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                reason = "signature_invalid",
                code = "SIGNATURE_INVALID",
                email = "secret-device-http@example.test",
                ipHash = "198.51.100.7"
            }),
            CreatedAt = now.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        using var client = environment.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        var adminSession = await environment.LoginAsync(admin.Handle);
        using (var request = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/admin/users/{target.Id}/devices"))
        {
            request.Headers.Add("Cookie", adminSession);
            using var response = await client.SendAsync(request);
            var html = await response.Content.ReadAsStringAsync();
            var text = WebUtility.HtmlDecode(html);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(target.Handle, text, StringComparison.Ordinal);
            Assert.Contains(device.Fingerprint, text, StringComparison.Ordinal);
            Assert.Contains("管理员历史页测试设备", text, StringComparison.Ordinal);
            Assert.Contains("SIGNATURE_INVALID", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-device-http@example.test", text, StringComparison.Ordinal);
            Assert.DoesNotContain("198.51.100.7", text, StringComparison.Ordinal);
            Assert.DoesNotContain(device.PublicKey, text, StringComparison.Ordinal);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Contains("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        }

        var memberSession = await environment.LoginAsync(member.Handle);
        using (var request = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/admin/users/{target.Id}/devices"))
        {
            request.Headers.Add("Cookie", memberSession);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var request = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/admin/users/{Guid.NewGuid()}/devices"))
        {
            request.Headers.Add("Cookie", adminSession);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}