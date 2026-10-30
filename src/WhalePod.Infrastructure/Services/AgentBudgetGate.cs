using Microsoft.Extensions.Logging;

using WhalePod.Domain;

namespace WhalePod.Infrastructure.Services;

internal sealed class AgentBudgetGate(
    AiSettingsService aiSettings,
    AiBudgetService aiBudget,
    ILogger<AgentModerationService> logger)
{
    public async Task<AiSettingsData> RequireEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await aiSettings.GetAsync(cancellationToken);
        if (!settings.Enabled)
        {
            throw AgentModerationService.AgentDisabled();
        }

        return settings;
    }

    public async Task EnsureNewWorkAllowedAsync(
        AiSettingsData settings,
        AgentIdentity identity,
        AuditContext audit,
        CancellationToken cancellationToken)
    {
        var budget = await aiBudget.GetAsync(settings, cancellationToken);
        if (budget.BudgetExceeded)
        {
            await aiBudget.RecordBudgetBlockAsync(audit, budget, cancellationToken);
            throw AiBudgetService.BudgetExceeded(budget);
        }

        if (budget.TaskBudgetExceeded)
        {
            await aiBudget.RecordBudgetBlockAsync(
                audit,
                budget,
                "daily_task_budget_exceeded",
                AiBudgetService.TaskBudgetExceededAction,
                cancellationToken);
            throw AiBudgetService.TaskBudgetExceeded(budget);
        }

        var accountBudget = await aiBudget.GetAccountStatusAsync(
            identity.ServiceAccountId,
            cancellationToken);
        if (accountBudget.BudgetExceeded)
        {
            await aiBudget.RecordAccountBudgetBlockAsync(audit, accountBudget, cancellationToken);
            throw AiBudgetService.AccountBudgetExceeded(accountBudget);
        }

        if (budget.FailureCircuitOpen)
        {
            logger.LogWarning(
                "ai_failure_circuit_open serviceAccountId={ServiceAccountId} recentFailures={RecentFailures} threshold={Threshold} windowMinutes={WindowMinutes}",
                identity.ServiceAccountId,
                budget.RecentFailures,
                AiBudgetService.FailureThreshold,
                AiBudgetService.FailureWindowMinutes);
            throw AiBudgetService.FailureCircuitOpen(budget);
        }
    }
}