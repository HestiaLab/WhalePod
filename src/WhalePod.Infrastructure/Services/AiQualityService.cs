using Microsoft.EntityFrameworkCore;

using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record AiQualityBucketView(
    string Key,
    int Total,
    int Decided,
    int Adopted);

public sealed record AiQualityStatsView(
    int Total,
    int Pending,
    int Decided,
    int Adopted,
    int Overridden,
    decimal AgreementRate,
    IReadOnlyList<AiQualityBucketView> BySuggestion,
    IReadOnlyList<AiQualityBucketView> ByModel,
    DateTimeOffset? LastSuggestionAt,
    bool QualityAlert,
    string? QualityAlertText);

public sealed class AiQualityService(WhalePodDbContext db)
{
    public Task<AiQualityStatsView> GetAsync(CancellationToken cancellationToken = default)
    {
        return GetCoreAsync(null, MinDecidedForAlert, MinAgreementRate, cancellationToken);
    }

    public Task<AiQualityStatsView> GetAsync(
        Guid serviceAccountId,
        CancellationToken cancellationToken = default)
    {
        return GetCoreAsync(serviceAccountId, MinDecidedForAlert, MinAgreementRate, cancellationToken);
    }

    public Task<AiQualityStatsView> GetAsync(
        int minDecided,
        decimal minAgreementRate,
        CancellationToken cancellationToken = default)
    {
        return GetCoreAsync(null, minDecided, minAgreementRate, cancellationToken);
    }

    public Task<AiQualityStatsView> GetAsync(
        Guid serviceAccountId,
        int minDecided,
        decimal minAgreementRate,
        CancellationToken cancellationToken = default)
    {
        return GetCoreAsync(serviceAccountId, minDecided, minAgreementRate, cancellationToken);
    }

    private async Task<AiQualityStatsView> GetCoreAsync(
        Guid? serviceAccountId,
        int minDecidedForAlert,
        decimal minAgreementRate,
        CancellationToken cancellationToken)
    {
        var query = db.ModerationSuggestions.AsNoTracking();
        if (serviceAccountId is not null)
        {
            query = query.Where(suggestion => suggestion.ServiceAccountId == serviceAccountId);
        }

        var rows = await query
            .Select(suggestion => new
            {
                suggestion.Suggestion,
                suggestion.HumanDecision,
                suggestion.ModelName,
                suggestion.ModelVersion,
                suggestion.ModelQuantization,
                suggestion.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var decided = rows.Count(row => row.HumanDecision is not null);
        var adopted = rows.Count(row => row.HumanDecision is not null
            && string.Equals(row.HumanDecision, row.Suggestion, StringComparison.Ordinal));
        var overridden = decided - adopted;
        var bySuggestion = rows
            .GroupBy(row => row.Suggestion, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var groupDecided = group.Count(row => row.HumanDecision is not null);
                return new AiQualityBucketView(
                    group.Key,
                    group.Count(),
                    groupDecided,
                    group.Count(row => row.HumanDecision is not null
                        && string.Equals(row.HumanDecision, row.Suggestion, StringComparison.Ordinal)));
            })
            .ToList();
        var byModel = rows
            .GroupBy(
                row => $"{row.ModelName}|{row.ModelVersion}|{row.ModelQuantization}",
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var groupDecided = group.Count(row => row.HumanDecision is not null);
                return new AiQualityBucketView(
                    group.Key,
                    group.Count(),
                    groupDecided,
                    group.Count(row => row.HumanDecision is not null
                        && string.Equals(row.HumanDecision, row.Suggestion, StringComparison.Ordinal)));
            })
            .ToList();

        var qualityAlert = decided >= minDecidedForAlert
            && decided > 0
            && (decimal)adopted / decided < minAgreementRate;
        return new AiQualityStatsView(
            rows.Count,
            rows.Count(row => row.HumanDecision is null),
            decided,
            adopted,
            overridden,
            decided == 0 ? 0 : Math.Round((decimal)adopted / decided, 3),
            bySuggestion,
            byModel,
            rows.Count == 0 ? null : rows.Max(row => row.CreatedAt),
            qualityAlert,
            qualityAlert
                ? $"已裁决样本 {decided} 条，人工一致率低于 {minAgreementRate:P0}，请暂停 AI 建议并复核。"
                : null);
    }

    public const int MinDecidedForAlert = 20;
    public const decimal MinAgreementRate = 0.6m;
}