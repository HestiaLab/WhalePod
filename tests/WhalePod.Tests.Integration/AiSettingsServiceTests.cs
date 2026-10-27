using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Persistence;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class AiSettingsServiceTests
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("WHALEPOD_TEST_DB_CONNECTION")
        ?? throw new InvalidOperationException("WHALEPOD_TEST_DB_CONNECTION is required.");

    private static WhalePodDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WhalePodDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WhalePodDbContext(options);
    }

    [DatabaseFact]
    public async Task Enable_requires_models_confirmation_and_persists_audited_settings()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await ResetAiSettingsAsync(db);

        var admin = NewUser("ai-admin", UserRoles.Admin);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var defaults = await service.GetAsync();
        Assert.False(defaults.Enabled);
        Assert.False(defaults.SuggestionsPaused);
        Assert.Empty(defaults.ApprovedModels);
        Assert.Equal(AiSettingsService.DefaultDailyTokenBudget, defaults.DailyTokenBudget);
        Assert.Equal(AiSettingsService.DefaultDailyTaskBudget, defaults.DailyTaskBudget);
        Assert.Equal(AiSettingsService.DefaultDailyContentBudget, defaults.DailyContentBudget);
        Assert.Equal(AiSettingsService.DefaultQualityMinDecided, defaults.QualityMinDecided);
        Assert.Equal(AiSettingsService.DefaultQualityMinAgreement, defaults.QualityMinAgreement);

        var noModels = await service.UpdateAsync(
            enabled: true,
            suggestionsPaused: false,
            approvedModels: [],
            dailyTokenBudget: 3_000_000,
            confirmEnable: true,
            NewAudit(admin.Id));
        Assert.Equal(AiSettingsOutcome.InvalidInput, noModels.Outcome);
        Assert.Equal("models", noModels.ErrorCode);

        var pendingModel = await service.UpdateAsync(
            enabled: true,
            suggestionsPaused: false,
            approvedModels: [new ApprovedModel("deepseek-flash", "pending-Q-02", "q4")],
            dailyTokenBudget: 3_000_000,
            confirmEnable: true,
            NewAudit(admin.Id));
        Assert.Equal(AiSettingsOutcome.InvalidInput, pendingModel.Outcome);

        var model = new ApprovedModel("deepseek-flash", "v1", "q4");
        var noConfirm = await service.UpdateAsync(
            enabled: true,
            suggestionsPaused: false,
            approvedModels: [model],
            dailyTokenBudget: 3_000_000,
            confirmEnable: false,
            NewAudit(admin.Id));
        Assert.Equal(AiSettingsOutcome.ConfirmationRequired, noConfirm.Outcome);

        var enabled = await service.UpdateAsync(
            enabled: true,
            suggestionsPaused: false,
            approvedModels: [model],
            dailyTokenBudget: 3_000_000,
            confirmEnable: true,
            NewAudit(admin.Id));
        Assert.Equal(AiSettingsOutcome.Success, enabled.Outcome);

        var stored = await CreateService(db).GetAsync();
        Assert.True(stored.Enabled);
        Assert.False(stored.SuggestionsPaused);
        Assert.Equal(new[] { model }, stored.ApprovedModels);
        Assert.Equal(3_000_000, stored.DailyTokenBudget);

        var qualityUpdated = await service.UpdateAsync(
            enabled: true,
            suggestionsPaused: false,
            approvedModels: [model],
            dailyTokenBudget: 3_000_000,
            dailyTaskBudget: AiSettingsService.DefaultDailyTaskBudget,
            dailyContentBudget: AiSettingsService.DefaultDailyContentBudget,
            qualityMinDecided: 50,
            qualityMinAgreement: 0.75m,
            confirmEnable: false,
            audit: NewAudit(admin.Id));
        Assert.Equal(AiSettingsOutcome.Success, qualityUpdated.Outcome);
        var qualitySettings = await CreateService(db).GetAsync();
        Assert.Equal(50, qualitySettings.QualityMinDecided);
        Assert.Equal(0.75m, qualitySettings.QualityMinAgreement);

        db.ChangeTracker.Clear();
        var settings = await db.SiteSettings
            .Where(setting => setting.Key.StartsWith("ai."))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value.RootElement.GetRawText());
        Assert.Equal("true", settings["ai.enabled"]);
        Assert.Contains("deepseek-flash", settings["ai.approved_models"]);
        Assert.Equal("3000000", settings["ai.daily_token_budget"]);

        var audits = await db.AuditLogs
            .Where(log => log.ActorId == admin.Id)
            .Select(log => log.Action)
            .ToListAsync();
        Assert.Contains("ai.enabled_changed", audits);
        Assert.Contains("ai.model_approved", audits);
        Assert.Contains("ai.budget_changed", audits);
        Assert.Contains("ai.quality_threshold_changed", audits);
    }

    [DatabaseFact]
    public async Task Pause_and_disable_are_audited_and_non_admin_is_forbidden()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await ResetAiSettingsAsync(db);

        var admin = NewUser("ai-switch", UserRoles.Admin);
        var member = NewUser("ai-member", UserRoles.Member);
        db.Users.AddRange(admin, member);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var model = new ApprovedModel("deepseek-flash", "v1", "q4");
        Assert.Equal(
            AiSettingsOutcome.Success,
            (await service.UpdateAsync(
                true,
                false,
                [model],
                2_000_000,
                true,
                NewAudit(admin.Id))).Outcome);

        Assert.Equal(
            AiSettingsOutcome.Success,
            (await service.UpdateAsync(
                true,
                true,
                [model],
                2_000_000,
                false,
                NewAudit(admin.Id))).Outcome);
        Assert.True((await service.GetAsync()).SuggestionsPaused);

        Assert.Equal(
            AiSettingsOutcome.Success,
            (await service.UpdateAsync(
                false,
                true,
                [model],
                2_000_000,
                false,
                NewAudit(admin.Id))).Outcome);
        var disabled = await service.GetAsync();
        Assert.False(disabled.Enabled);
        Assert.False(disabled.SuggestionsPaused);

        Assert.Equal(
            AiSettingsOutcome.Forbidden,
            (await service.UpdateAsync(
                false,
                false,
                [model],
                2_000_000,
                false,
                NewAudit(member.Id))).Outcome);

        db.ChangeTracker.Clear();
        Assert.Contains(
            await db.AuditLogs.Where(log => log.ActorId == admin.Id).Select(log => log.Action).ToListAsync(),
            action => action == "ai.suggestions_paused_changed");
    }

    [DatabaseFact]
    public async Task Invalid_budget_and_model_patterns_are_rejected()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await ResetAiSettingsAsync(db);

        var admin = NewUser("ai-invalid", UserRoles.Admin);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var model = new ApprovedModel("deepseek-flash", "v1", "q4");

        var badBudget = await service.UpdateAsync(
            false,
            false,
            [model],
            0,
            false,
            NewAudit(admin.Id));
        Assert.Equal("budget", badBudget.ErrorCode);

        var badName = await service.UpdateAsync(
            false,
            false,
            [new ApprovedModel("Deepseek-flash", "v1", "q4")],
            2_000_000,
            false,
            NewAudit(admin.Id));
        Assert.Equal("models", badName.ErrorCode);

        var pending = await service.UpdateAsync(
            false,
            false,
            [new ApprovedModel("deepseek-flash", "v1", "pending-Q-02")],
            2_000_000,
            false,
            NewAudit(admin.Id));
        Assert.Equal("models", pending.ErrorCode);

        var badTaskBudget = await service.UpdateAsync(
            false,
            false,
            [model],
            2_000_000,
            0,
            2_000_000,
            false,
            NewAudit(admin.Id));
        Assert.Equal("task-budget", badTaskBudget.ErrorCode);

        var badContentBudget = await service.UpdateAsync(
            false,
            false,
            [model],
            2_000_000,
            10_000,
            0,
            false,
            NewAudit(admin.Id));
        Assert.Equal("content-budget", badContentBudget.ErrorCode);
    }

    private static AiSettingsService CreateService(WhalePodDbContext db)
    {
        return new AiSettingsService(db, new AdminAccessService(db), TimeProvider.System);
    }

    private static async Task ResetAiSettingsAsync(WhalePodDbContext db)
    {
        var existing = await db.SiteSettings
            .Where(setting => setting.Key.StartsWith("ai."))
            .ToListAsync();
        if (existing.Count > 0)
        {
            db.SiteSettings.RemoveRange(existing);
            await db.SaveChangesAsync();
        }
    }

    private static AuditContext NewAudit(Guid actorId)
    {
        return new AuditContext(actorId, "test-request", new byte[32]);
    }

    private static User NewUser(string prefix, string role)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new User
        {
            Handle = $"{prefix}-{suffix}",
            DisplayName = "测试用户",
            Status = UserStatuses.Active,
            Role = role,
            ActivatedAt = DateTimeOffset.UtcNow
        };
    }
}