using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Unit;

public sealed class BackupStatusProviderTests
{
    [Fact]
    public void Unconfigured_status_file_reports_not_configured()
    {
        var provider = CreateProvider(null);
        var status = provider.Read();
        Assert.False(status.Configured);
        Assert.Null(status.LastSuccessAt);
    }

    [Fact]
    public void Valid_status_file_is_parsed_without_exposing_paths()
    {
        var path = Path.Combine(Path.GetTempPath(), $"whalepod-backup-status-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                path,
                """
                {"formatVersion":1,"lastSuccessUtc":"2026-10-26T02:47:31Z","database":"whalepod","fileName":"whalepod-whalepod.dump","bytes":128495,"sha256":"abc"}
                """);
            var provider = CreateProvider(path);
            var status = provider.Read();
            Assert.True(status.Configured);
            Assert.Equal(DateTimeOffset.Parse("2026-10-26T02:47:31Z"), status.LastSuccessAt);
            Assert.Equal("whalepod", status.Database);
            Assert.Equal(128495, status.Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Invalid_status_file_is_configured_but_has_no_timestamp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"whalepod-backup-status-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{not-json");
            var provider = CreateProvider(path);
            var status = provider.Read();
            Assert.True(status.Configured);
            Assert.Null(status.LastSuccessAt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static BackupStatusProvider CreateProvider(string? path)
    {
        var configuration = new TestConfiguration(
            path is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["Operations:Backup:StatusFilePath"] = path });
        return new BackupStatusProvider(
            configuration,
            NullLogger<BackupStatusProvider>.Instance);
    }

    private sealed class TestConfiguration(Dictionary<string, string?> values) : IConfiguration
    {
        public string? this[string key]
        {
            get => values.GetValueOrDefault(key);
            set => values[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);

        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }
}