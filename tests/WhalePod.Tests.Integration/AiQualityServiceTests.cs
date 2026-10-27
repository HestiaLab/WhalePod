using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class AiQualityServiceTests
{
    [DatabaseFact]
    public async Task Quality_alert_triggers_after_minimum_sample_and_recovers_above_threshold()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("quality-admin", UserRoles.Admin);
        var account = new ServiceAccount
        {
            Name = $"quality-agent-{suffix}",
            OwnerId = admin.Id,
            Scopes = ["moderation:suggest"],
            Status = ServiceAccountStatuses.Active
        };
        var instance = new AgentInstance
        {
            ServiceAccountId = account.Id,
            InstanceName = $"quality-instance-{suffix}",
            HostFingerprint = new string('e', 64),
            RuntimeLocation = RuntimeLocations.Local,
            AgentVersion = "1.0.0",
            ModelName = "deepseek-flash",
            ModelVersion = "v1",
            ModelQuantization = "q4",
            ModelRuntime = RuntimeLocations.Local,
            Status = AgentInstanceStatuses.Active
        };
        var task = new ModerationTask
        {
            TargetType = ModerationTargetTypes.Post,
            TargetId = Guid.NewGuid(),
            ContentVersion = 1,
            ContentHash = new string('f', 64),
            Status = ModerationTaskStatuses.Suggested,
            Priority = 10,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            db.Users.Add(admin);
            db.ServiceAccounts.Add(account);
            db.AgentInstances.Add(instance);
            db.ModerationTasks.Add(task);
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 10; index++)
            {
                db.ModerationSuggestions.Add(NewSuggestion(
                    account, instance, task, $"quality-adopt-{suffix}-{index}",
                    "approve", "approve", admin.Id, now));
                db.ModerationSuggestions.Add(NewSuggestion(
                    account, instance, task, $"quality-override-{suffix}-{index}",
                    "approve", "reject", admin.Id, now));
            }

            await db.SaveChangesAsync();

            var service = new AiQualityService(db);
            var alert = await service.GetAsync(account.Id);
            Assert.Equal(20, alert.Decided);
            Assert.Equal(10, alert.Adopted);
            Assert.Equal(0.5m, alert.AgreementRate);
            Assert.True(alert.QualityAlert);
            Assert.NotNull(alert.QualityAlertText);
            Assert.False((await service.GetAsync(account.Id, 20, 0.4m)).QualityAlert);
            Assert.True((await service.GetAsync(account.Id, 10, 0.6m)).QualityAlert);

            for (var index = 0; index < 10; index++)
            {
                db.ModerationSuggestions.Add(NewSuggestion(
                    account, instance, task, $"quality-extra-{suffix}-{index}",
                    "approve", "approve", admin.Id, now));
            }

            await db.SaveChangesAsync();
            var recovered = await service.GetAsync(account.Id);
            Assert.Equal(30, recovered.Decided);
            Assert.Equal(0.667m, recovered.AgreementRate);
            Assert.False(recovered.QualityAlert);
            Assert.True((await service.GetAsync(account.Id, 30, 0.7m)).QualityAlert);
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.ModerationSuggestions
                .Where(suggestion => suggestion.ServiceAccountId == account.Id)
                .ExecuteDeleteAsync();
            await db.ModerationTasks.Where(candidate => candidate.Id == task.Id).ExecuteDeleteAsync();
            await db.AgentInstances
                .Where(candidate => candidate.ServiceAccountId == account.Id)
                .ExecuteDeleteAsync();
            await db.ServiceAccounts.Where(candidate => candidate.Id == account.Id).ExecuteDeleteAsync();
            await db.Users.Where(candidate => candidate.Id == admin.Id).ExecuteDeleteAsync();
        }
    }

    private static ModerationSuggestion NewSuggestion(
        ServiceAccount account,
        AgentInstance instance,
        ModerationTask task,
        string idempotencyKey,
        string suggestion,
        string humanDecision,
        Guid adminId,
        DateTimeOffset now)
    {
        return new ModerationSuggestion
        {
            TaskId = task.Id,
            ServiceAccountId = account.Id,
            AgentInstanceId = instance.Id,
            Suggestion = suggestion,
            PolicyCode = "manual_review",
            Reason = "质量统计测试",
            Confidence = 0.8m,
            RuntimeLocation = RuntimeLocations.Local,
            ModelRuntime = RuntimeLocations.Local,
            ModelName = "deepseek-flash",
            ModelVersion = "v1",
            ModelQuantization = "q4",
            PromptVersion = "prompt-v1",
            PolicyVersion = AgentPolicyDefaults.Version,
            TokenUsage = JsonDocument.Parse("""{"total":10}"""),
            IdempotencyKey = idempotencyKey,
            HumanDecision = humanDecision,
            HumanDecisionBy = adminId,
            HumanDecisionAt = now,
            CreatedAt = now
        };
    }
}