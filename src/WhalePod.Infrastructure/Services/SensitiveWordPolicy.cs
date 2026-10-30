using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record SensitiveWordEvaluation(
    string Policy,
    IReadOnlyList<string> MatchedTerms,
    bool Reject,
    DateTimeOffset? PolicyUpdatedAt)
{
    public bool HasMatches => MatchedTerms.Count > 0;
}

public static class SensitiveWordPolicy
{
    public static async Task<SensitiveWordEvaluation> EvaluateAsync(
        WhalePodDbContext db,
        string text,
        CancellationToken cancellationToken = default)
    {
        var setting = await db.SiteSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Key == SensitiveWordAdminService.PolicyKey,
                cancellationToken);
        var policy = SensitiveWordAdminService.ReadPolicyHit(setting);
        var words = await db.SensitiveWords
            .AsNoTracking()
            .Where(word => word.Status == SensitiveWordStatuses.Active)
            .Select(word => new { word.Term, word.Action })
            .ToListAsync(cancellationToken);
        var matched = words
            .Where(word => text.Contains(word.Term, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var terms = matched
            .Select(word => word.Term)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(term => term, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var reject = terms.Count > 0
            && (policy == SensitiveWordActions.Reject
                || matched.Any(word => word.Action == SensitiveWordActions.Reject));
        return new SensitiveWordEvaluation(
            policy,
            terms,
            reject,
            setting?.UpdatedAt);
    }

    public static ModerationCheck CreateCheck(
        string targetType,
        Guid targetId,
        int contentVersion,
        SensitiveWordEvaluation evaluation,
        DateTimeOffset now)
    {
        return new ModerationCheck
        {
            TargetType = targetType,
            TargetId = targetId,
            ContentVersion = contentVersion,
            Provider = "local-sensitive-word",
            CheckType = ModerationCheckTypes.Text,
            Result = evaluation.Reject
                ? ModerationCheckResults.Reject
                : ModerationCheckResults.Review,
            RiskLabels = JsonSerializer.SerializeToDocument(evaluation.MatchedTerms),
            RawResponse = JsonSerializer.SerializeToDocument(new
            {
                policy = evaluation.Policy,
                policyUpdatedAt = evaluation.PolicyUpdatedAt,
                matchedTerms = evaluation.MatchedTerms
            }),
            LatencyMs = 0,
            CreatedAt = now
        };
    }
}