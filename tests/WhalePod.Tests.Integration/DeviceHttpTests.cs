using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using WhalePod.Contracts.Devices;
using WhalePod.Domain;
using WhalePod.Domain.Accounts;

namespace WhalePod.Tests.Integration;

[Collection("api-http")]
public sealed class DeviceHttpTests(ApiHttpEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [DatabaseFact]
    public async Task Device_endpoints_enforce_authentication_csrf_and_account_state()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var member = ServiceTestSupport.NewUser("http-device-member");
        var pending = ServiceTestSupport.NewUser("http-device-pending", activated: false);
        var muted = ServiceTestSupport.NewUser(
            "http-device-muted",
            status: UserStatuses.Muted,
            statusExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
            statusReason: "设备测试禁言");
        var banned = ServiceTestSupport.NewUser("http-device-banned");
        var deleted = ServiceTestSupport.NewUser("http-device-deleted");
        var admin = ServiceTestSupport.NewUser("http-device-admin", UserRoles.Admin);
        db.Users.AddRange(member, pending, muted, banned, deleted, admin);
        await db.SaveChangesAsync();

        using var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using (var anonymous = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/devices/challenge",
                   null,
                   null,
                   null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("AUTH_REQUIRED", await ReadCodeAsync(anonymous));
        }

        var memberSession = await environment.LoginAsync(member.Handle);
        var (memberCsrf, memberAntiforgery) = await environment.ApiFactory.GetCsrfAsync(memberSession);
        using (var missingCsrf = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/devices/challenge",
                   null,
                   memberSession,
                   null,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(missingCsrf));
        }

        using (var memberChallenge = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/devices/challenge",
                   null,
                   memberSession,
                   memberCsrf,
                   memberAntiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, memberChallenge.StatusCode);
            Assert.Contains("community-device-v1", await memberChallenge.Content.ReadAsStringAsync());
        }

        var pendingSession = await environment.LoginAsync(pending.Handle);
        var (pendingCsrf, pendingAntiforgery) = await environment.ApiFactory.GetCsrfAsync(pendingSession);
        using (var pendingChallenge = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/devices/challenge",
                   null,
                   pendingSession,
                   pendingCsrf,
                   pendingAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, pendingChallenge.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(pendingChallenge));
        }

        var mutedSession = await environment.LoginAsync(muted.Handle);
        var (mutedCsrf, mutedAntiforgery) = await environment.ApiFactory.GetCsrfAsync(mutedSession);
        using (var mutedChallenge = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/devices/challenge",
                   null,
                   mutedSession,
                   mutedCsrf,
                   mutedAntiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, mutedChallenge.StatusCode);
        }

        var bannedSession = await environment.LoginAsync(banned.Handle);
        var bannedEntity = await db.Users.SingleAsync(user => user.Id == banned.Id);
        bannedEntity.Status = UserStatuses.Banned;
        bannedEntity.StatusReason = "设备测试封禁";
        await db.SaveChangesAsync();
        var (bannedCsrf, bannedAntiforgery) = await environment.ApiFactory.GetCsrfAsync(bannedSession);
        using (var bannedChallenge = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/devices/challenge",
                   null,
                   bannedSession,
                   bannedCsrf,
                   bannedAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, bannedChallenge.StatusCode);
            Assert.Equal("ACCOUNT_BANNED", await ReadCodeAsync(bannedChallenge));
        }

        var deletedSession = await environment.LoginAsync(deleted.Handle);
        var deletedEntity = await db.Users.SingleAsync(user => user.Id == deleted.Id);
        deletedEntity.Status = UserStatuses.Deleted;
        deletedEntity.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var (deletedCsrf, deletedAntiforgery) = await environment.ApiFactory.GetCsrfAsync(deletedSession);
        using (var deletedChallenge = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/devices/challenge",
                   null,
                   deletedSession,
                   deletedCsrf,
                   deletedAntiforgery))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deletedChallenge.StatusCode);
            Assert.Equal("FORBIDDEN", await ReadCodeAsync(deletedChallenge));
        }

        var adminSession = await environment.LoginAsync(admin.Handle);
        var (adminCsrf, adminAntiforgery) = await environment.ApiFactory.GetCsrfAsync(adminSession);
        using (var adminChallenge = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/v1/devices/challenge",
                   null,
                   adminSession,
                   adminCsrf,
                   adminAntiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, adminChallenge.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task Cli_output_completes_device_and_revoke_updates_badge()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var member = ServiceTestSupport.NewUser("http-device-cli");
        db.Users.Add(member);
        await db.SaveChangesAsync();

        using var client = environment.ApiFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var session = await environment.LoginAsync(member.Handle);
        var (csrf, antiforgery) = await environment.ApiFactory.GetCsrfAsync(session);
        using var challengeResponse = await SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/devices/challenge",
            null,
            session,
            csrf,
            antiforgery);
        Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
        using var challengeDocument = JsonDocument.Parse(
            await challengeResponse.Content.ReadAsStringAsync());
        var challenge = challengeDocument.RootElement
            .GetProperty("data")
            .GetProperty("challenge")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(challenge));

        var cliDll = ResolveCliDll();
        Assert.True(File.Exists(cliDll), $"Device CLI not found at {cliDll}. Build the solution first.");
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"whalepod-device-cli-{Guid.NewGuid():N}");
        var keyFile = Path.Combine(keyDirectory, "key.json");
        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(
                cliDll,
                "sign",
                "--challenge",
                challenge!,
                "--handle",
                member.Handle,
                "--key-file",
                keyFile);
            Assert.True(exitCode == 0, $"CLI exited {exitCode}. stderr: {stderr}");
            using var cliDocument = JsonDocument.Parse(stdout);
            Assert.Equal(DeviceProtocol.Version, cliDocument.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(member.Handle, cliDocument.RootElement.GetProperty("handle").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                cliDocument.RootElement.GetProperty("publicKey").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(
                cliDocument.RootElement.GetProperty("signature").GetString()));

            using var complete = await SendRawJsonAsync(
                client,
                "/api/v1/devices/complete",
                stdout,
                session,
                csrf,
                antiforgery);
            Assert.Equal(HttpStatusCode.Created, complete.StatusCode);
            using var completeDocument = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
            var device = completeDocument.RootElement.GetProperty("data").GetProperty("device");
            Assert.NotEqual(Guid.Empty, device.GetProperty("id").GetGuid());
            Assert.True(completeDocument.RootElement
                .GetProperty("data")
                .GetProperty("badge")
                .GetProperty("verified")
                .GetBoolean());
            var deviceId = device.GetProperty("id").GetGuid();

            using (var list = await SendAsync(
                       client,
                       HttpMethod.Get,
                       "/api/v1/devices",
                       null,
                       session,
                       null))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                using var listDocument = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                var data = listDocument.RootElement.GetProperty("data");
                Assert.Equal(1, data.GetProperty("activeDeviceCount").GetInt32());
                Assert.Equal(5, data.GetProperty("maxActiveDevices").GetInt32());
                Assert.Equal(1, data.GetProperty("items").GetArrayLength());
            }

            using (var revoke = await SendAsync(
                       client,
                       HttpMethod.Delete,
                       $"/api/v1/devices/{deviceId}",
                       null,
                       session,
                       csrf,
                       antiforgery))
            {
                Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
                using var revokeDocument = JsonDocument.Parse(await revoke.Content.ReadAsStringAsync());
                Assert.Equal(0, revokeDocument.RootElement
                    .GetProperty("data")
                    .GetProperty("activeDeviceCount")
                    .GetInt32());
                Assert.Equal(
                    DeviceStatuses.Revoked,
                    revokeDocument.RootElement.GetProperty("data").GetProperty("status").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    [DatabaseFact]
    public async Task Challenge_endpoint_enforces_rate_limit()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var member = ServiceTestSupport.NewUser("http-device-rate");
        db.Users.Add(member);
        await db.SaveChangesAsync();

        var previousLimit = Environment.GetEnvironmentVariable(
            "DeviceVerification__ChallengeRateLimitPerMinute");
        Environment.SetEnvironmentVariable(
            "DeviceVerification__ChallengeRateLimitPerMinute",
            "1");
        try
        {
            using var limitedFactory = new WhalePodApiFactory(environment.KeysDirectory);
            using var client = limitedFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false
            });
            var session = await environment.LoginAsync(member.Handle);
            var (csrf, antiforgery) = await limitedFactory.GetCsrfAsync(session);

            using (var first = await SendAsync(
                       client,
                       HttpMethod.Post,
                       "/api/v1/devices/challenge",
                       null,
                       session,
                       csrf,
                       antiforgery))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            using (var second = await SendAsync(
                       client,
                       HttpMethod.Post,
                       "/api/v1/devices/challenge",
                       null,
                       session,
                       csrf,
                       antiforgery))
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
                Assert.Equal("RATE_LIMITED", await ReadCodeAsync(second));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "DeviceVerification__ChallengeRateLimitPerMinute",
                previousLimit);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string cliDll,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(cliDll);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the Device CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Device CLI did not exit within 10 seconds.");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static string ResolveCliDll()
    {
        var candidates = new[] { "Release", "Debug" }
            .Select(configuration => Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "WhalePod.DeviceCli",
                "bin",
                configuration,
                "net10.0",
                "whalepod-device.dll")))
            .ToList();
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static async Task<HttpResponseMessage> SendRawJsonAsync(
        HttpClient client,
        string path,
        string json,
        string? sessionCookie,
        string? csrfToken,
        string? antiforgeryCookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (sessionCookie is not null)
        {
            request.Headers.Add(
                "Cookie",
                antiforgeryCookie is null
                    ? sessionCookie
                    : $"{sessionCookie}; {antiforgeryCookie}");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrfToken);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body,
        string? sessionCookie,
        string? csrfToken,
        string? antiforgeryCookie = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        if (sessionCookie is not null)
        {
            request.Headers.Add(
                "Cookie",
                antiforgeryCookie is null
                    ? sessionCookie
                    : $"{sessionCookie}; {antiforgeryCookie}");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrfToken);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return document.RootElement.GetProperty("code").GetString();
    }
}