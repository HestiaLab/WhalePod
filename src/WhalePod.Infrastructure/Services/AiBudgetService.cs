using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using WhalePod.Domain;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record AiBudgetStatusView(
    bool Enabled,
    bool SuggestionsPaused,
    int DailyTokenBudget,
    int DailyTaskBudget,
    int DailyContentBudget,
    long UsedTokensToday,
    long RemainingTokens,
    int TodaySuggestionCount,
    int TodayCreatedTaskCount,
    bool BudgetExceeded,
    bool TaskBudgetExceeded,
    bool ContentBudgetExceeded,
    int RecentFailures,
    bool FailureCircuitOpen,
    bool BlockingNewWork,
    DateTimeOffset DayStartedAt,
    DateTimeOffset ResetsAt,
    DateTimeOffset CheckedAt);

public sealed record AiAccountBudgetStatus(
    Guid ServiceAccountId,
    int? OverrideBudget,
    long UsedTokensToday,
    int TodaySuggestionCount,
    bool BudgetExceeded,
    DateTimeOffset DayStartedAt,
    DateTimeOffset ResetsAt,
    DateTimeOffset CheckedAt);

public static class AiTokenUsage
{
    public static long Sum(JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "total", StringComparison.OrdinalIgnoreCase)
                && TryReadNonNegative(property.Value, out var total))
            {
                return total;
            }
        }

        var sum = 0L;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (TryReadNonNegative(property.Value, out var value))
            {
                sum = SaturatingAdd(sum, value);
            }
        }

        return sum;
    }

    private static bool TryReadNonNegative(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && value >= 0;
    }

    internal static long SaturatingAdd(long left, long right)
    {
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }
}

public sealed class AiBudgetService(
    WhalePodDbContext db,
    AiSettingsService aiSettings,
    TimeProvider timeProvider,
    ILogger<AiBudgetService> logger)
{
    public const string TokenBudgetExceededAction = "ai.token_budget_exceeded";
    public const string AccountTokenBudgetExceededAction = "ai.service_account_token_budget_exceeded";
    public const string TaskBudgetExceededAction = "ai.task_budget_exceeded";
    public const string ContentBudgetExceededAction = "ai.content_budget_exceeded";
    public const string LeaseExpiredAction = "agent.lease_expired";
    public const string SuggestionFailedAction = "agent.suggestion_failed";
    public const string SuggestionSubmittedAction = "agent.suggestion_submitted";
    public const int FailureThreshold = 5;
    public const int FailureWindowMinutes = 15;

    public Task<AiBudgetStatusView> GetAsync(CancellationToken cancellationToken = default)
    {
        return GetCoreAsync(null, cancellationToken);
    }

    public Task<AiBudgetStatusView> GetAsync(
        AiSettingsData settings,
        CancellationToken cancellationToken = default)
    {
        return GetCoreAsync(settings, cancellationToken);
    }

    public Task RecordBudgetBlockAsync(
        AuditContext audit,
        AiBudgetStatusView status,
        CancellationToken cancellationToken = default)
    {
        return RecordBudgetBlockAsync(
            audit,
            status,
            "daily_token_budget_exceeded",
            TokenBudgetExceededAction,
            cancellationToken);
    }

    public async Task RecordBudgetBlockAsync(
        AuditContext audit,
        AiBudgetStatusView status,
        string reason,
        string action,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "ai_budget_blocked reason={Reason} serviceAccountId={ServiceAccountId} usedTokens={UsedTokens} dailyTokenBudget={DailyTokenBudget} suggestions={SuggestionCount}/{TaskBudget} tasks={TaskCount}/{ContentBudget} resetsAt={ResetsAt}",
            reason,
            audit.ActorId,
            status.UsedTokensToday,
            status.DailyTokenBudget,
            status.TodaySuggestionCount,
            status.DailyTaskBudget,
            status.TodayCreatedTaskCount,
            status.DailyContentBudget,
            status.ResetsAt);

        var alreadyRecorded = await db.AuditLogs
            .AsNoTracking()
            .AnyAsync(
                log => log.Action == action
                    && log.CreatedAt >= status.DayStartedAt
                    && log.CreatedAt < status.ResetsAt,
                cancellationToken);
        if (alreadyRecorded)
        {
            return;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.Service,
            ActorId = audit.ActorId,
            Action = action,
            TargetType = "site_setting",
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                reason,
                usedTokens = status.UsedTokensToday,
                dailyTokenBudget = status.DailyTokenBudget,
                todaySuggestions = status.TodaySuggestionCount,
                dailyTaskBudget = status.DailyTaskBudget,
                todayTasks = status.TodayCreatedTaskCount,
                dailyContentBudget = status.DailyContentBudget,
                resetsAt = status.ResetsAt
            }),
            CreatedAt = status.CheckedAt
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static ApiProblemException BudgetExceeded(AiBudgetStatusView status)
    {
        var retryAfterSeconds = Math.Max(
            1,
            (int)Math.Ceiling((status.ResetsAt - status.CheckedAt).TotalSeconds));
        return new ApiProblemException(
            ApiErrorCodes.AgentRateLimited,
            429,
            "AI 每日 token 预算已用尽，请改由人工审核或等待预算重置。",
            new
            {
                reason = "daily_token_budget_exceeded",
                usedTokens = status.UsedTokensToday,
                dailyTokenBudget = status.DailyTokenBudget,
                resetsAt = status.ResetsAt
            },
            retryAfterSeconds);
    }

    public async Task<AiAccountBudgetStatus> GetAccountStatusAsync(
        Guid serviceAccountId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var resetsAt = dayStart.AddDays(1);
        var tokenUsages = await db.ModerationSuggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.ServiceAccountId == serviceAccountId
                && suggestion.CreatedAt >= dayStart
                && suggestion.CreatedAt < resetsAt)
            .Select(suggestion => suggestion.TokenUsage)
            .ToListAsync(cancellationToken);
        var usedTokens = 0L;
        foreach (var usage in tokenUsages)
        {
            usedTokens = AiTokenUsage.SaturatingAdd(usedTokens, AiTokenUsage.Sum(usage));
        }

        var overrideBudget = await db.ServiceAccounts
            .AsNoTracking()
            .Where(account => account.Id == serviceAccountId)
            .Select(account => account.DailyTokenBudget)
            .SingleOrDefaultAsync(cancellationToken);
        var exceeded = overrideBudget is { } budget && usedTokens >= budget;
        return new AiAccountBudgetStatus(
            serviceAccountId,
            overrideBudget,
            usedTokens,
            tokenUsages.Count,
            exceeded,
            dayStart,
            resetsAt,
            now);
    }

    public async Task RecordAccountBudgetBlockAsync(
        AuditContext audit,
        AiAccountBudgetStatus status,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "ai_account_token_budget_exceeded serviceAccountId={ServiceAccountId} usedTokens={UsedTokens} overrideBudget={OverrideBudget} resetsAt={ResetsAt}",
            status.ServiceAccountId,
            status.UsedTokensToday,
            status.OverrideBudget,
            status.ResetsAt);
        var alreadyRecorded = await db.AuditLogs
            .AsNoTracking()
            .AnyAsync(
                log => log.Action == AccountTokenBudgetExceededAction
                    && log.ActorId == status.ServiceAccountId
                    && log.CreatedAt >= status.DayStartedAt
                    && log.CreatedAt < status.ResetsAt,
                cancellationToken);
        if (alreadyRecorded)
        {
            return;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.Service,
            ActorId = status.ServiceAccountId,
            Action = AccountTokenBudgetExceededAction,
            TargetType = "service_account",
            TargetId = status.ServiceAccountId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                usedTokens = status.UsedTokensToday,
                dailyTokenBudget = status.OverrideBudget,
                resetsAt = status.ResetsAt
            }),
            CreatedAt = status.CheckedAt
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static ApiProblemException AccountBudgetExceeded(AiAccountBudgetStatus status)
    {
        var retryAfterSeconds = Math.Max(
            1,
            (int)Math.Ceiling((status.ResetsAt - status.CheckedAt).TotalSeconds));
        return new ApiProblemException(
            ApiErrorCodes.AgentRateLimited,
            429,
            "该服务账号的每日 token 预算已用尽，请改由人工审核或等待预算重置。",
            new
            {
                reason = "service_account_token_budget_exceeded",
                usedTokens = status.UsedTokensToday,
                dailyTokenBudget = status.OverrideBudget,
                resetsAt = status.ResetsAt
            },
            retryAfterSeconds);
    }

    public static ApiProblemException TaskBudgetExceeded(AiBudgetStatusView status)
    {
        return BudgetLimitExceeded(
            status,
            "daily_task_budget_exceeded",
            "AI 每日任务量预算已用尽，请改由人工审核或等待预算重置。",
            status.TodaySuggestionCount,
            status.DailyTaskBudget);
    }

    public static ApiProblemException ContentBudgetExceeded(AiBudgetStatusView status)
    {
        return BudgetLimitExceeded(
            status,
            "daily_content_budget_exceeded",
            "AI 每日内容量预算已用尽，请改由人工审核或等待预算重置。",
            status.TodayCreatedTaskCount,
            status.DailyContentBudget);
    }

    private static ApiProblemException BudgetLimitExceeded(
        AiBudgetStatusView status,
        string reason,
        string message,
        int used,
        int limit)
    {
        var retryAfterSeconds = Math.Max(
            1,
            (int)Math.Ceiling((status.ResetsAt - status.CheckedAt).TotalSeconds));
        return new ApiProblemException(
            ApiErrorCodes.AgentRateLimited,
            429,
            message,
            new { reason, used, limit, resetsAt = status.ResetsAt },
            retryAfterSeconds);
    }

    public static ApiProblemException FailureCircuitOpen(AiBudgetStatusView status)
    {
        return new ApiProblemException(
            ApiErrorCodes.AgentDisabled,
            503,
            "AI 审核已因连续失败熔断，请改由人工审核。",
            new
            {
                reason = "agent_failure_circuit",
                recentFailures = status.RecentFailures,
                threshold = FailureThreshold,
                windowMinutes = FailureWindowMinutes
            });
    }

    private async Task<AiBudgetStatusView> GetCoreAsync(
        AiSettingsData? settingsOverride,
        CancellationToken cancellationToken)
    {
        var settings = settingsOverride ?? await aiSettings.GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var resetsAt = dayStart.AddDays(1);
        var tokenUsages = await db.ModerationSuggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.CreatedAt >= dayStart && suggestion.CreatedAt < resetsAt)
            .Select(suggestion => suggestion.TokenUsage)
            .ToListAsync(cancellationToken);

        var usedTokens = 0L;
        foreach (var usage in tokenUsages)
        {
            usedTokens = AiTokenUsage.SaturatingAdd(usedTokens, AiTokenUsage.Sum(usage));
        }

        var budget = settings.DailyTokenBudget > 0
            ? settings.DailyTokenBudget
            : AiSettingsService.DefaultDailyTokenBudget;
        var taskBudget = settings.DailyTaskBudget > 0
            ? settings.DailyTaskBudget
            : AiSettingsService.DefaultDailyTaskBudget;
        var contentBudget = settings.DailyContentBudget > 0
            ? settings.DailyContentBudget
            : AiSettingsService.DefaultDailyContentBudget;
        var createdTaskCount = await db.ModerationTasks
            .AsNoTracking()
            .CountAsync(
                task => task.CreatedAt >= dayStart && task.CreatedAt < resetsAt,
                cancellationToken);
        var failureFrom = now.AddMinutes(-FailureWindowMinutes);
        var lastSuccessAt = await db.AuditLogs
            .AsNoTracking()
            .Where(log => log.Action == SuggestionSubmittedAction
                && log.CreatedAt >= failureFrom
                && log.CreatedAt <= now)
            .MaxAsync(log => (DateTimeOffset?)log.CreatedAt, cancellationToken);
        var countedFrom = lastSuccessAt is not null && lastSuccessAt > failureFrom
            ? lastSuccessAt.Value
            : failureFrom;
        var failureLogs = await db.AuditLogs
            .AsNoTracking()
            .Where(log => (log.Action == LeaseExpiredAction
                    || log.Action == SuggestionFailedAction)
                && log.CreatedAt > countedFrom
                && log.CreatedAt <= now)
            .Select(log => log.MetadataJson)
            .ToListAsync(cancellationToken);
        var recentFailures = 0;
        foreach (var metadata in failureLogs)
        {
            recentFailures = (int)Math.Min(
                int.MaxValue,
                (long)recentFailures + ReadFailureCount(metadata));
        }

        var exceeded = usedTokens >= budget;
        var taskExceeded = tokenUsages.Count >= taskBudget;
        var contentExceeded = createdTaskCount >= contentBudget;
        var circuitOpen = recentFailures >= FailureThreshold;
        return new AiBudgetStatusView(
            settings.Enabled,
            settings.SuggestionsPaused,
            budget,
            taskBudget,
            contentBudget,
            usedTokens,
            Math.Max(0, budget - usedTokens),
            tokenUsages.Count,
            createdTaskCount,
            exceeded,
            taskExceeded,
            contentExceeded,
            recentFailures,
            circuitOpen,
            exceeded || taskExceeded || contentExceeded || circuitOpen,
            dayStart,
            resetsAt,
            now);
    }

    private static long ReadFailureCount(JsonDocument? metadata)
    {
        if (metadata is null
            || metadata.RootElement.ValueKind != JsonValueKind.Object
            || !metadata.RootElement.TryGetProperty("count", out var count)
            || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt64(out var value)
            || value < 1)
        {
            return 1;
        }

        return value;
    }
}