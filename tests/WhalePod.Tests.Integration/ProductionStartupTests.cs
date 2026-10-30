using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WhalePod.Tests.Integration;

public sealed class ProductionStartupTests
{
    [Fact]
    public void Production_startup_requires_github_oauth_configuration()
    {
        var settings = MinimalProductionSettings();
        settings["GitHub:ClientId"] = string.Empty;
        settings["GitHub:ClientSecret"] = string.Empty;

        using var scope = new SettingsScope(settings);
        using var factory = new ProductionFactory();
        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Contains("GitHub:ClientId", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_startup_rejects_dev_login()
    {
        var settings = MinimalProductionSettings();
        settings["Auth:DevLoginEnabled"] = "true";

        using var scope = new SettingsScope(settings);
        using var factory = new ProductionFactory();
        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Contains("Auth:DevLoginEnabled", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_startup_succeeds_with_required_configuration()
    {
        using var scope = new SettingsScope(MinimalProductionSettings());
        using var factory = new ProductionFactory();
        Assert.NotNull(factory.Services);
    }

    [Fact]
    public void Production_startup_requires_explicit_allowed_hosts()
    {
        var settings = MinimalProductionSettings();
        settings["AllowedHosts"] = "*";

        using var scope = new SettingsScope(settings);
        using var factory = new ProductionFactory();
        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Contains("AllowedHosts", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_startup_requires_audit_ip_hash_secret()
    {
        var settings = MinimalProductionSettings();
        settings["Audit:IpHashSecret"] = string.Empty;

        using var scope = new SettingsScope(settings);
        using var factory = new ProductionFactory();
        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Contains("Audit:IpHashSecret", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_startup_requires_data_protection_keys_directory()
    {
        var settings = MinimalProductionSettings();
        settings["DataProtection:KeysDirectory"] = string.Empty;

        using var scope = new SettingsScope(settings);
        using var factory = new ProductionFactory();
        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Contains("DataProtection:KeysDirectory", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_startup_requires_https_email_base_url()
    {
        var settings = MinimalProductionSettings();
        settings["Email:PublicBaseUrl"] = "http://whalepod.example.test";

        using var scope = new SettingsScope(settings);
        using var factory = new ProductionFactory();
        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Contains("HTTPS", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_startup_rejects_plaintext_smtp()
    {
        var settings = MinimalProductionSettings();
        settings["Email:Smtp:Security"] = "None";

        using var scope = new SettingsScope(settings);
        using var factory = new ProductionFactory();
        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Contains("Email:Smtp:Security", Flatten(exception), StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> MinimalProductionSettings()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"whalepod-production-dpkeys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);
        return new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["AllowedHosts"] = "whalepod.example.test;api.whalepod.example.test",
            ["ConnectionStrings:WhalePod"] = "Host=127.0.0.1;Port=5432;Database=whalepod_production;Username=app",
            ["PublicSite:BaseUrl"] = "https://whalepod.example.test",
            ["Audit:IpHashSecret"] = new string('a', 64),
            ["ViewCounter:DedupeSecret"] = new string('b', 64),
            ["CopyCounter:DedupeSecret"] = new string('c', 64),
            ["DataProtection:KeysDirectory"] = keysDirectory,
            ["GitHub:ClientId"] = "client",
            ["GitHub:ClientSecret"] = "secret",
            ["Auth:DevLoginEnabled"] = "false",
            ["MediaStorage:Provider"] = "S3",
            ["MediaStorage:PublicBaseUrl"] = "https://cdn.example.test/media",
            ["MediaStorage:S3:ServiceUrl"] = "https://cos.example.test",
            ["MediaStorage:S3:BucketName"] = "whalepod-test",
            ["Email:Provider"] = "Smtp",
            ["Email:PublicBaseUrl"] = "https://whalepod.example.test",
            ["Email:SuppressionHashSecret"] = new string('d', 64),
            ["Email:UnsubscribeSecret"] = new string('e', 64),
            ["Email:Smtp:Host"] = "smtp.example.test",
            ["Email:Smtp:Port"] = "587",
            ["Email:Smtp:FromAddress"] = "no-reply@example.test"
        };
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private sealed class ProductionFactory : WebApplicationFactory<WhalePod.Web.Security.AdminRequirement>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
        }
    }

    private sealed class SettingsScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new();

        public SettingsScope(Dictionary<string, string?> settings)
        {
            foreach (var pair in settings)
            {
                var name = pair.Key.Contains(':')
                    ? pair.Key.Replace(":", "__")
                    : pair.Key;
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}