using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using WhalePod.Domain;

namespace WhalePod.Tests.Integration;

public sealed class ForwardedHeadersAuditTests
{
    private const string IpHashSecret = "test-only-audit-ip-hash-secret-0123456789";
    private const string ProxyAddress = "203.0.113.10";
    private const string ClientAddress = "198.51.100.42";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [DatabaseFact]
    public async Task Audit_ip_hash_uses_forwarded_client_address_only_from_trusted_proxy()
    {
        await AssertAuditAddressAsync(
            enabled: true,
            remoteIp: ProxyAddress,
            trustedProxy: ProxyAddress,
            expectForwardedClient: true);
    }

    [DatabaseFact]
    public async Task Audit_ip_hash_ignores_forwarded_header_from_untrusted_proxy()
    {
        await AssertAuditAddressAsync(
            enabled: true,
            remoteIp: "203.0.113.11",
            trustedProxy: ProxyAddress,
            expectForwardedClient: false);
    }

    [DatabaseFact]
    public async Task Audit_ip_hash_ignores_forwarded_header_when_forwarding_is_disabled()
    {
        await AssertAuditAddressAsync(
            enabled: false,
            remoteIp: ProxyAddress,
            trustedProxy: ProxyAddress,
            expectForwardedClient: false);
    }

    private static async Task AssertAuditAddressAsync(
        bool enabled,
        string remoteIp,
        string trustedProxy,
        bool expectForwardedClient)
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"whalepod-forwarded-dpkeys-{Guid.NewGuid():N}");
        using var environmentScope = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["ConnectionStrings__WhalePod"] = ServiceTestSupport.ConnectionString,
            ["DataProtection__KeysDirectory"] = keysDirectory,
            ["Audit__IpHashSecret"] = IpHashSecret,
            ["Auth__DevLoginEnabled"] = "true",
            ["ReverseProxy__ForwardedHeadersEnabled"] = enabled ? "true" : "false",
            ["ReverseProxy__ForwardLimit"] = "1",
            ["ReverseProxy__KnownProxies__0"] = trustedProxy
        });
        var overrides = new Dictionary<string, string?>
        {
            ["ReverseProxy:ForwardedHeadersEnabled"] = enabled ? "true" : "false",
            ["ReverseProxy:ForwardLimit"] = "1",
            ["ReverseProxy:KnownProxies:0"] = trustedProxy
        };
        var apiFactory = new WhalePodApiFactory(
            keysDirectory,
            overrides,
            builder => builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(remoteIp))));
        var webFactory = new WhalePodWebFactory(keysDirectory);
        try
        {
            await apiFactory.MigrateAsync();
            await using var db = ServiceTestSupport.CreateContext();
            var suffix = ServiceTestSupport.NewSuffix();
            var member = ServiceTestSupport.NewUser("forwarded-member");
            member.Handle = $"forwarded-member-{suffix}";
            var category = ServiceTestSupport.NewCategory();
            db.Users.Add(member);
            db.Categories.Add(category);
            await db.SaveChangesAsync();

            var session = await webFactory.LoginAsync(member.Handle);
            var (csrfToken, antiforgeryCookie) = await apiFactory.GetCsrfAsync(session);
            using var client = apiFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            title = $"转发头审计帖子 {suffix}",
                            categoryId = category.Id,
                            bodyMd = "用于验证审计 IP HMAC 采用可信转发地址的测试正文。"
                        },
                        JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Add("Cookie", $"{session}; {antiforgeryCookie}");
            request.Headers.Add("X-CSRF-Token", csrfToken);
            request.Headers.Add("X-Forwarded-For", ClientAddress);
            request.Headers.Add("X-Forwarded-Proto", "https");
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, responseBody);
            Assert.DoesNotContain(ClientAddress, responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain(
                ClientAddress,
                response.Headers.ToString(),
                StringComparison.Ordinal);

            Guid postId;
            using (var document = JsonDocument.Parse(responseBody))
            {
                postId = document.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            }

            db.ChangeTracker.Clear();
            var audit = await db.AuditLogs.SingleAsync(log =>
                log.TargetId == postId && log.Action == "post.created");
            Assert.NotNull(audit.IpHash);
            Assert.Equal(32, audit.IpHash!.Length);
            Assert.False(string.IsNullOrWhiteSpace(audit.RequestId));
            Assert.DoesNotContain(remoteIp, audit.MetadataJson.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(ClientAddress, audit.MetadataJson.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("forwarded", audit.MetadataJson.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

            var expectedAddress = expectForwardedClient ? ClientAddress : remoteIp;
            Assert.Equal(HashAddress(expectedAddress), audit.IpHash);
            if (expectForwardedClient)
            {
                Assert.NotEqual(HashAddress(ProxyAddress), audit.IpHash);
            }
            else
            {
                Assert.NotEqual(HashAddress(ClientAddress), audit.IpHash);
            }
        }
        finally
        {
            await webFactory.DisposeAsync();
            await apiFactory.DisposeAsync();
            try
            {
                Directory.Delete(keysDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static byte[] HashAddress(string address)
    {
        return HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(IpHashSecret),
            Encoding.UTF8.GetBytes(address));
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new();

        public EnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var pair in values)
            {
                _previousValues[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _previousValues)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    private sealed class RemoteIpStartupFilter(string remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
                    return nextMiddleware();
                });
                next(app);
            };
        }
    }
}