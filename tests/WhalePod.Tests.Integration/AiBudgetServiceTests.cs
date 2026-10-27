using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class AiBudgetServiceTests
{
    private static async Task<DateTimeOffset> FindCleanFakeNowAsync(WhalePodDbContext db)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var candidate = new DateTimeOffset(2100, 1, 1, 12, 0, 0, TimeSpan.Zero)
                .AddDays(Random.Shared.Next(0, 20_000));
            var dayStart = new DateTimeOffset(candidate.UtcDateTime.Date, TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1);
            if (await db.AuditLogs.AnyAsync(log =>
                    log.CreatedAt >= dayStart && log.CreatedAt < dayEnd)
                || await db.ModerationSuggestions.AnyAsync(suggestion =>
                    suggestion.CreatedAt >= dayStart && suggestion.CreatedAt < dayEnd)
                || await db.ModerationTasks.AnyAsync(task =>
                    task.CreatedAt >= dayStart && task.CreatedAt < dayEnd))
            {
                continue;
            }

            return candidate;
        }

        throw new InvalidOperationException("Unable to find an isolated fake day for budget tests.");
    }

    [DatabaseFact]
    public async Task Budget_and_failure_circuit_are_computed_from_real_records()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var fakeNow = await FindCleanFakeNowAsync(db);
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("budget-admin", UserRoles.Admin);
        var account = new ServiceAccount
        {
            Name = $"budget-agent-{suffix}",
            OwnerId = admin.Id,
            Scopes = ["moderation:suggest"],
            Status = ServiceAccountStatuses.Active
        };
        var instance = new AgentInstance
        {
            ServiceAccountId = account.Id,
            InstanceName = $"budget-instance-{suffix}",
            HostFingerprint = new string('a', 64),
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
            ContentHash = new string('b', 64),
            Status = ModerationTaskStatuses.Suggested,
            Priority = 10,
            CreatedAt = fakeNow
        };
        var time = new MutableTimeProvider(fakeNow);

        try
        {
            db.Users.Add(admin);
            db.ServiceAccounts.Add(account);
            db.AgentInstances.Add(instance);
            db.ModerationTasks.Add(task);
            await db.SaveChangesAsync();
            await ConfigureAsync(db, admin, 1000);

            var service = CreateService(db, time);
            var initial = await service.GetAsync();
            Assert.Equal(0, initial.UsedTokensToday);
            Assert.Equal(1000, initial.DailyTokenBudget);
            Assert.False(initial.BudgetExceeded);
            Assert.False(initial.FailureCircuitOpen);

            db.ModerationSuggestions.AddRange(
                NewSuggestion(account, instance, task, "budget-total-1", """{"prompt":100,"completion":20,"total":120}""", fakeNow),
                NewSuggestion(account, instance, task, "budget-fallback-2", """{"prompt":900}""", fakeNow));
            db.AuditLogs.AddRange(
                NewLeaseExpiryAudit(fakeNow.AddMinutes(-5), 3),
                NewLeaseExpiryAudit(fakeNow.AddMinutes(-4), 1),
                NewLeaseExpiryAudit(fakeNow.AddMinutes(-3), 1));
            await db.SaveChangesAsync();

            var status = await service.GetAsync();
            Assert.Equal(1020, status.UsedTokensToday);
            Assert.Equal(0, status.RemainingTokens);
            Assert.Equal(2, status.TodaySuggestionCount);
            Assert.True(status.BudgetExceeded);
            Assert.Equal(5, status.RecentFailures);
            Assert.True(status.FailureCircuitOpen);
            Assert.True(status.BlockingNewWork);

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = AuditActorTypes.Service,
                ActorId = account.Id,
                Action = AiBudgetService.SuggestionSubmittedAction,
                TargetType = "moderation_task",
                TargetId = task.Id,
                MetadataJson = JsonSerializer.SerializeToDocument(new { suggestion = "approve" }),
                CreatedAt = fakeNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
            var resetBySuccess = await service.GetAsync();
            Assert.Equal(0, resetBySuccess.RecentFailures);
            Assert.False(resetBySuccess.FailureCircuitOpen);

            var budgetError = AiBudgetService.BudgetExceeded(status);
            Assert.Equal(ApiErrorCodes.AgentRateLimited, budgetError.Code);
            Assert.Equal(429, budgetError.StatusCode);
            Assert.InRange(budgetError.RetryAfterSeconds ?? 0, 1, 86_400);

            var circuitError = AiBudgetService.FailureCircuitOpen(status);
            Assert.Equal(ApiErrorCodes.AgentDisabled, circuitError.Code);
            Assert.Equal(503, circuitError.StatusCode);

            var audit = new AuditContext(account.Id, "budget-test", new byte[32]);
            await service.RecordBudgetBlockAsync(audit, status);
            await service.RecordBudgetBlockAsync(audit, status);
            db.ChangeTracker.Clear();
            var budgetAudits = await db.AuditLogs
                .Where(log => log.Action == AiBudgetService.TokenBudgetExceededAction
                    && log.CreatedAt >= status.DayStartedAt
                    && log.CreatedAt < status.ResetsAt)
                .ToListAsync();
            Assert.Single(budgetAudits);
            Assert.Equal(
                1020,
                budgetAudits[0].MetadataJson.RootElement.GetProperty("usedTokens").GetInt64());

            time.UtcNow = fakeNow.AddMinutes(20);
            var recoveredCircuit = await service.GetAsync();
            Assert.Equal(0, recoveredCircuit.RecentFailures);
            Assert.False(recoveredCircuit.FailureCircuitOpen);
            Assert.True(recoveredCircuit.BudgetExceeded);

            await ConfigureAsync(db, admin, 5000);
            var recoveredBudget = await service.GetAsync();
            Assert.False(recoveredBudget.BudgetExceeded);
            Assert.Equal(3980, recoveredBudget.RemainingTokens);
            Assert.False(recoveredBudget.BlockingNewWork);
        }
        finally
        {
            await CleanupAsync(db, admin.Id, account.Id, task.Id);
        }
    }

    [DatabaseFact]
    public async Task Daily_task_and_content_budgets_are_enforced_and_audited()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var fakeNow = await FindCleanFakeNowAsync(db);
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = ServiceTestSupport.NewUser("budget-task-admin", UserRoles.Admin);
        var account = new ServiceAccount
        {
            Name = $"budget-task-agent-{suffix}",
            OwnerId = admin.Id,
            Scopes = ["moderation:suggest"],
            DailyTokenBudget = 5,
            Status = ServiceAccountStatuses.Active
        };
        var instance = new AgentInstance
        {
            ServiceAccountId = account.Id,
            InstanceName = $"budget-task-instance-{suffix}",
            HostFingerprint = new string('c', 64),
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
            ContentHash = new string('d', 64),
            Status = ModerationTaskStatuses.Suggested,
            Priority = 10,
            CreatedAt = fakeNow
        };

        try
        {
            db.Users.Add(admin);
            db.ServiceAccounts.Add(account);
            db.AgentInstances.Add(instance);
            db.ModerationTasks.Add(task);
            await db.SaveChangesAsync();
            await ConfigureAsync(db, admin, 1_000_000, taskBudget: 1, contentBudget: 1);

            var service = CreateService(db, new MutableTimeProvider(fakeNow));
            var initial = await service.GetAsync();
            Assert.Equal(1, initial.TodayCreatedTaskCount);
            Assert.True(initial.ContentBudgetExceeded);
            Assert.False(initial.TaskBudgetExceeded);

            db.ModerationSuggestions.Add(NewSuggestion(
                account,
                instance,
                task,
                $"budget-task-{suffix}",
                """{"total":10}""",
                fakeNow));
            await db.SaveChangesAsync();
            var status = await service.GetAsync();
            Assert.Equal(1, status.TodaySuggestionCount);
            Assert.True(status.TaskBudgetExceeded);
            Assert.True(status.ContentBudgetExceeded);
            Assert.True(status.BlockingNewWork);
            Assert.Equal(429, AiBudgetService.TaskBudgetExceeded(status).StatusCode);
            Assert.Equal(429, AiBudgetService.ContentBudgetExceeded(status).StatusCode);

            var accountStatus = await service.GetAccountStatusAsync(account.Id);
            Assert.Equal(5, accountStatus.OverrideBudget);
            Assert.Equal(10, accountStatus.UsedTokensToday);
            Assert.True(accountStatus.BudgetExceeded);
            Assert.Equal(429, AiBudgetService.AccountBudgetExceeded(accountStatus).StatusCode);

            var audit = new AuditContext(account.Id, "budget-task-test", new byte[32]);
            await service.RecordBudgetBlockAsync(
                audit,
                status,
                "daily_task_budget_exceeded",
                AiBudgetService.TaskBudgetExceededAction);
            await service.RecordBudgetBlockAsync(
                audit,
                status,
                "daily_task_budget_exceeded",
                AiBudgetService.TaskBudgetExceededAction);
            db.ChangeTracker.Clear();
            Assert.Equal(
                1,
                await db.AuditLogs.CountAsync(log =>
                    log.Action == AiBudgetService.TaskBudgetExceededAction
                    && log.CreatedAt >= status.DayStartedAt
                    && log.CreatedAt < status.ResetsAt));
            await service.RecordAccountBudgetBlockAsync(audit, accountStatus);
            await service.RecordAccountBudgetBlockAsync(audit, accountStatus);
            db.ChangeTracker.Clear();
            Assert.Equal(
                1,
                await db.AuditLogs.CountAsync(log =>
                    log.Action == AiBudgetService.AccountTokenBudgetExceededAction
                    && log.ActorId == account.Id
                    && log.CreatedAt >= status.DayStartedAt
                    && log.CreatedAt < status.ResetsAt));
        }
        finally
        {
            await CleanupAsync(db, admin.Id, account.Id, task.Id);
        }
    }

    [DatabaseFact]
    public async Task Missing_or_non_positive_budget_falls_back_to_default_instead_of_unlimited()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var fakeNow = await FindCleanFakeNowAsync(db);
        var time = new MutableTimeProvider(fakeNow);
        var service = CreateService(db, time);
        try
        {
            await ResetAiSettingsAsync(db);
            db.SiteSettings.Add(new SiteSetting
            {
                Key = "ai.daily_token_budget",
                Value = JsonSerializer.SerializeToDocument(0),
                UpdatedAt = fakeNow
            });
            await db.SaveChangesAsync();

            var status = await service.GetAsync();
            Assert.Equal(AiSettingsService.DefaultDailyTokenBudget, status.DailyTokenBudget);

            var setting = await db.SiteSettings
                .SingleAsync(candidate => candidate.Key == "ai.daily_token_budget");
            setting.Value = JsonSerializer.SerializeToDocument(-10);
            await db.SaveChangesAsync();
            Assert.Equal(
                AiSettingsService.DefaultDailyTokenBudget,
                (await service.GetAsync()).DailyTokenBudget);
        }
        finally
        {
            await ResetAiSettingsAsync(db);
        }
    }

    private static AiBudgetService CreateService(WhalePodDbContext db, TimeProvider timeProvider)
    {
        var settings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
        return new AiBudgetService(
            db,
            settings,
            timeProvider,
            NullLogger<AiBudgetService>.Instance);
    }

    private static async Task ConfigureAsync(
        WhalePodDbContext db,
        User admin,
        int budget,
        int? taskBudget = null,
        int? contentBudget = null)
    {
        var settings = new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
        var result = await settings.UpdateAsync(
            enabled: true,
            suggestionsPaused: false,
            approvedModels: [new ApprovedModel("deepseek-flash", "v1", "q4")],
            dailyTokenBudget: budget,
            dailyTaskBudget: taskBudget ?? AiSettingsService.DefaultDailyTaskBudget,
            dailyContentBudget: contentBudget ?? AiSettingsService.DefaultDailyContentBudget,
            confirmEnable: true,
            audit: new AuditContext(admin.Id, "budget-test", new byte[32]));
        Assert.True(result.Outcome is AiSettingsOutcome.Success or AiSettingsOutcome.Unchanged);
    }

    private static ModerationSuggestion NewSuggestion(
        ServiceAccount account,
        AgentInstance instance,
        ModerationTask task,
        string idempotencyKey,
        string tokenUsage,
        DateTimeOffset createdAt)
    {
        return new ModerationSuggestion
        {
            TaskId = task.Id,
            ServiceAccountId = account.Id,
            AgentInstanceId = instance.Id,
            Suggestion = SuggestionTypes.Approve,
            PolicyCode = "manual_review",
            Reason = "预算测试建议",
            Confidence = 0.8m,
            RuntimeLocation = RuntimeLocations.Local,
            ModelRuntime = RuntimeLocations.Local,
            ModelName = "deepseek-flash",
            ModelVersion = "v1",
            ModelQuantization = "q4",
            PromptVersion = "prompt-v1",
            PolicyVersion = AgentPolicyDefaults.Version,
            TokenUsage = JsonDocument.Parse(tokenUsage),
            IdempotencyKey = idempotencyKey,
            CreatedAt = createdAt
        };
    }

    private static AuditLog NewLeaseExpiryAudit(DateTimeOffset createdAt, int count)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.System,
            Action = AiBudgetService.LeaseExpiredAction,
            TargetType = "agent_task_lease",
            MetadataJson = JsonSerializer.SerializeToDocument(new { count }),
            CreatedAt = createdAt
        };
    }

    private static async Task ResetAiSettingsAsync(WhalePodDbContext db)
    {
        db.ChangeTracker.Clear();
        await db.SiteSettings
            .Where(setting => setting.Key.StartsWith("ai."))
            .ExecuteDeleteAsync();
    }

    private static async Task CleanupAsync(
        WhalePodDbContext db,
        Guid adminId,
        Guid accountId,
        Guid taskId)
    {
        db.ChangeTracker.Clear();
        await db.ModerationSuggestions
            .Where(suggestion => suggestion.ServiceAccountId == accountId)
            .ExecuteDeleteAsync();
        await db.ModerationTasks.Where(task => task.Id == taskId).ExecuteDeleteAsync();
        await db.AgentInstances
            .Where(instance => instance.ServiceAccountId == accountId)
            .ExecuteDeleteAsync();
        await db.ServiceAccounts.Where(account => account.Id == accountId).ExecuteDeleteAsync();
        await ResetAiSettingsAsync(db);
        await db.Users.Where(user => user.Id == adminId).ExecuteDeleteAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }
}