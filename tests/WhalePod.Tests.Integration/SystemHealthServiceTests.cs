using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Tests.Integration;

public sealed class SystemHealthServiceTests
{
    private static WhalePodDbContext CreateContext() => ServiceTestSupport.CreateContext();

    [DatabaseFact]
    public async Task Health_warns_when_backup_is_stale_and_survives_invalid_status_file()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        var path = Path.Combine(
            Path.GetTempPath(),
            $"whalepod-health-backup-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                $$"""{"lastSuccessUtc":"{{DateTimeOffset.UtcNow.AddHours(-48):O}}","database":"whalepod","bytes":1}""");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Operations:Backup:StatusFilePath"] = path
                })
                .Build();
            var aiSettings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
            var service = new SystemHealthService(
                db,
                aiSettings,
                new AiBudgetService(
                    db,
                    aiSettings,
                    TimeProvider.System,
                    NullLogger<AiBudgetService>.Instance),
                Options.Create(new RetentionOptions()),
                Options.Create(new HealthOptions()),
                Options.Create(new MediaStorageOptions { Provider = MediaStorageProviders.Local }),
                new BackupStatusProvider(
                    configuration,
                    NullLogger<BackupStatusProvider>.Instance),
                TimeProvider.System,
                NullLogger<SystemHealthService>.Instance);

            var stale = await service.GetAsync();
            Assert.True(stale.DatabaseReachable);
            Assert.NotNull(stale.LastBackupAt);
            Assert.True(stale.LastBackupAt < DateTimeOffset.UtcNow.AddHours(-47));
            Assert.Contains(stale.Warnings, warning => warning.Contains("超过 24 小时", StringComparison.Ordinal));

            await File.WriteAllTextAsync(path, "[1,2,3]");
            var invalid = await service.GetAsync();
            Assert.True(invalid.DatabaseReachable);
            Assert.Null(invalid.LastBackupAt);
            Assert.DoesNotContain(
                invalid.Warnings,
                warning => warning.Contains("数据库不可达", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [DatabaseFact]
    public async Task Health_reports_database_queue_agents_and_moderation_metrics()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        db.EmailOutbox.RemoveRange(await db.EmailOutbox.ToListAsync());
        db.ModerationSuggestions.RemoveRange(await db.ModerationSuggestions.ToListAsync());
        db.ModerationTasks.RemoveRange(await db.ModerationTasks.ToListAsync());
        db.AgentInstances.RemoveRange(await db.AgentInstances.ToListAsync());
        db.ModerationChecks.RemoveRange(await db.ModerationChecks.ToListAsync());
        await db.SaveChangesAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var owner = new User
        {
            Handle = $"health-{suffix}",
            DisplayName = "健康检查用户",
            Status = UserStatuses.Active,
            Role = UserRoles.Member,
            ActivatedAt = now
        };
        var account = new ServiceAccount
        {
            Name = $"health-agent-{suffix}",
            OwnerId = owner.Id,
            Scopes = ["agent:heartbeat"],
            Status = ServiceAccountStatuses.Active
        };

        db.Users.Add(owner);
        db.ServiceAccounts.Add(account);
        db.EmailOutbox.AddRange(
            NewEmail($"pending-{suffix}@example.test", EmailStatuses.Pending, now.AddMinutes(-20)),
            NewEmail($"sending-{suffix}@example.test", EmailStatuses.Sending, now.AddMinutes(-15)),
            NewEmail($"failed-{suffix}@example.test", EmailStatuses.Failed, now.AddMinutes(-10)),
            NewEmail($"sent-{suffix}@example.test", EmailStatuses.Sent, now.AddMinutes(-5)),
            NewEmail($"suppressed-{suffix}@example.test", EmailStatuses.Suppressed, now.AddMinutes(-1)));

        db.AgentInstances.AddRange(
            NewAgent(account.Id, $"online-{suffix}", now.AddMinutes(-1), AgentInstanceStatuses.Active),
            NewAgent(account.Id, $"offline-{suffix}", now.AddMinutes(-30), AgentInstanceStatuses.Active),
            NewAgent(account.Id, $"paused-{suffix}", now.AddMinutes(-60), AgentInstanceStatuses.Paused));

        db.ModerationChecks.AddRange(
            NewModerationCheck(ModerationCheckResults.Review, now.AddHours(-1)),
            NewModerationCheck(ModerationCheckResults.Error, now.AddHours(-2)),
            NewModerationCheck(ModerationCheckResults.Error, now.AddHours(-30)));
        await db.SaveChangesAsync();

        var aiSettings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
        var service = new SystemHealthService(
            db,
            aiSettings,
            new AiBudgetService(
                db,
                aiSettings,
                TimeProvider.System,
                NullLogger<AiBudgetService>.Instance),
            Options.Create(new RetentionOptions()),
            Options.Create(new HealthOptions()),
            Options.Create(new MediaStorageOptions { Provider = MediaStorageProviders.S3 }),
            new BackupStatusProvider(
                new ConfigurationBuilder().Build(),
                NullLogger<BackupStatusProvider>.Instance),
            TimeProvider.System,
            NullLogger<SystemHealthService>.Instance);

        var health = await service.GetAsync();

        Assert.True(health.DatabaseReachable);
        Assert.Equal(0, health.PendingMigrations);
        Assert.True(health.AppliedMigrations > 0);
        Assert.Equal(1, health.EmailPending);
        Assert.Equal(1, health.EmailSending);
        Assert.Equal(1, health.EmailFailed);
        Assert.Equal(1, health.EmailSuppressed);
        Assert.Equal(1, health.EmailSent);
        Assert.NotNull(health.OldestEmailBacklogAt);
        Assert.Equal(2, health.ActiveAgents);
        Assert.Equal(1, health.OfflineAgents);
        Assert.NotNull(health.LastAgentHeartbeatAt);
        Assert.Equal(2, health.ModerationChecks24h);
        Assert.Equal(1, health.ModerationErrors24h);
        Assert.False(health.BackupConfigured);
        Assert.True(health.StorageConfigured);
        Assert.Equal(MediaStorageProviders.S3, health.StorageProvider);
        Assert.True(health.AiDailyTokenBudget > 0);
        Assert.False(health.AiBudgetExceeded);
        Assert.False(health.AiFailureCircuitOpen);
        Assert.Contains(health.Warnings, warning => warning.Contains("邮件", StringComparison.Ordinal));
        Assert.Contains(health.Warnings, warning => warning.Contains("Agent", StringComparison.Ordinal));
        Assert.Contains(health.Warnings, warning => warning.Contains("机审", StringComparison.Ordinal));
        Assert.Contains(health.Warnings, warning => warning.Contains("备份", StringComparison.Ordinal));
    }

    private static EmailOutbox NewEmail(string address, string status, DateTimeOffset createdAt)
    {
        return new EmailOutbox
        {
            ToAddress = address,
            Template = "test",
            Status = status,
            CreatedAt = createdAt,
            NextAttemptAt = createdAt
        };
    }

    private static AgentInstance NewAgent(
        Guid accountId,
        string name,
        DateTimeOffset heartbeat,
        string status)
    {
        return new AgentInstance
        {
            ServiceAccountId = accountId,
            InstanceName = name,
            HostFingerprint = new string('a', 64),
            RuntimeLocation = RuntimeLocations.Local,
            AgentVersion = "1.0.0",
            ModelName = "deepseek-flash",
            ModelVersion = "v1",
            ModelQuantization = "q4",
            ModelRuntime = RuntimeLocations.Local,
            LastHeartbeatAt = heartbeat,
            Status = status
        };
    }

    private static ModerationCheck NewModerationCheck(string result, DateTimeOffset createdAt)
    {
        return new ModerationCheck
        {
            TargetType = ModerationTargetTypes.Post,
            TargetId = Guid.NewGuid(),
            ContentVersion = 1,
            Provider = "test",
            CheckType = ModerationCheckTypes.Text,
            Result = result,
            CreatedAt = createdAt
        };
    }
}