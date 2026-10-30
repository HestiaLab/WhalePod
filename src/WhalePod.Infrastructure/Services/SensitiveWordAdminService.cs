using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Moderation;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum SensitiveWordOutcome
{
    Success,
    Unchanged,
    NotFound,
    Forbidden,
    InvalidInput,
    DuplicateTerm
}

public sealed record SensitiveWordListItem(
    Guid Id,
    string Term,
    string Action,
    string Status,
    string? CreatedByHandle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SensitiveWordList(
    IReadOnlyList<SensitiveWordListItem> Items,
    string PolicyHit);

public sealed class SensitiveWordAdminService(
    WhalePodDbContext db,
    AdminAccessService adminAccess,
    TimeProvider timeProvider)
{
    public const string PolicyKey = "moderation.sensitive_words_policy";

    public async Task<SensitiveWordList> GetAsync(CancellationToken cancellationToken = default)
    {
        var rows = await (
                from word in db.SensitiveWords.AsNoTracking()
                join creator in db.Users.AsNoTracking() on word.CreatedBy equals (Guid?)creator.Id into creators
                from creator in creators.DefaultIfEmpty()
                orderby word.Status, word.Term
                select new
                {
                    word.Id,
                    word.Term,
                    word.Action,
                    word.Status,
                    CreatedByHandle = creator != null ? creator.Handle : null,
                    word.CreatedAt,
                    word.UpdatedAt
                })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new SensitiveWordListItem(
                row.Id,
                row.Term,
                row.Action,
                row.Status,
                row.CreatedByHandle,
                row.CreatedAt,
                row.UpdatedAt))
            .ToList();

        var setting = await db.SiteSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Key == PolicyKey, cancellationToken);

        return new SensitiveWordList(items, ReadPolicyHit(setting));
    }

    public async Task<SensitiveWordOutcome> CreateAsync(
        string? term,
        string? action,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return SensitiveWordOutcome.Forbidden;
        }

        if (!TryNormalizeTerm(term, out var normalizedTerm)
            || !TryNormalizeAction(action, out var normalizedAction))
        {
            return SensitiveWordOutcome.InvalidInput;
        }

        var now = timeProvider.GetUtcNow();
        var word = new SensitiveWord
        {
            Term = normalizedTerm,
            Action = normalizedAction,
            Status = SensitiveWordStatuses.Active,
            CreatedBy = audit.ActorId,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.SensitiveWords.Add(word);
        db.AuditLogs.Add(NewAudit(
            audit,
            "sensitive_word.created",
            word.Id,
            new { action = normalizedAction, status = word.Status },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
        {
            SqlState: "23505",
            ConstraintName: "uq_sensitive_word_term"
        })
        {
            db.ChangeTracker.Clear();
            return SensitiveWordOutcome.DuplicateTerm;
        }

        return SensitiveWordOutcome.Success;
    }

    public async Task<SensitiveWordOutcome> UpdateAsync(
        Guid wordId,
        string? term,
        string? action,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return SensitiveWordOutcome.Forbidden;
        }

        if (!TryNormalizeTerm(term, out var normalizedTerm)
            || !TryNormalizeAction(action, out var normalizedAction))
        {
            return SensitiveWordOutcome.InvalidInput;
        }

        var word = await db.SensitiveWords
            .SingleOrDefaultAsync(candidate => candidate.Id == wordId, cancellationToken);
        if (word is null)
        {
            return SensitiveWordOutcome.NotFound;
        }

        if (string.Equals(word.Term, normalizedTerm, StringComparison.Ordinal)
            && word.Action == normalizedAction)
        {
            return SensitiveWordOutcome.Unchanged;
        }

        var now = timeProvider.GetUtcNow();
        word.Term = normalizedTerm;
        word.Action = normalizedAction;
        word.UpdatedAt = now;

        db.AuditLogs.Add(NewAudit(
            audit,
            "sensitive_word.updated",
            word.Id,
            new { action = normalizedAction, status = word.Status },
            now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
        {
            SqlState: "23505",
            ConstraintName: "uq_sensitive_word_term"
        })
        {
            db.ChangeTracker.Clear();
            return SensitiveWordOutcome.DuplicateTerm;
        }

        return SensitiveWordOutcome.Success;
    }

    public async Task<SensitiveWordOutcome> DisableAsync(
        Guid wordId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return SensitiveWordOutcome.Forbidden;
        }

        var word = await db.SensitiveWords
            .SingleOrDefaultAsync(candidate => candidate.Id == wordId, cancellationToken);
        if (word is null)
        {
            return SensitiveWordOutcome.NotFound;
        }

        if (word.Status == SensitiveWordStatuses.Disabled)
        {
            return SensitiveWordOutcome.Unchanged;
        }

        var now = timeProvider.GetUtcNow();
        word.Status = SensitiveWordStatuses.Disabled;
        word.UpdatedAt = now;

        db.AuditLogs.Add(NewAudit(
            audit,
            "sensitive_word.disabled",
            word.Id,
            new { status = word.Status },
            now));

        await db.SaveChangesAsync(cancellationToken);
        return SensitiveWordOutcome.Success;
    }

    public async Task<SensitiveWordOutcome> UpdatePolicyAsync(
        string? hit,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return SensitiveWordOutcome.Forbidden;
        }

        if (!TryNormalizeAction(hit, out var normalizedHit))
        {
            return SensitiveWordOutcome.InvalidInput;
        }

        var setting = await db.SiteSettings
            .SingleOrDefaultAsync(candidate => candidate.Key == PolicyKey, cancellationToken);
        var currentHit = ReadPolicyHit(setting);
        if (string.Equals(currentHit, normalizedHit, StringComparison.Ordinal))
        {
            return SensitiveWordOutcome.Unchanged;
        }

        var now = timeProvider.GetUtcNow();
        var value = JsonSerializer.SerializeToDocument(new { hit = normalizedHit });
        if (setting is null)
        {
            db.SiteSettings.Add(new SiteSetting
            {
                Key = PolicyKey,
                Value = value,
                UpdatedAt = now,
                UpdatedBy = audit.ActorId
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
            setting.UpdatedBy = audit.ActorId;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = audit.ActorId,
            Action = "moderation.policy_changed",
            TargetType = "site_setting",
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new { hit = normalizedHit }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        return SensitiveWordOutcome.Success;
    }

    internal static string ReadPolicyHit(SiteSetting? setting)
    {
        if (setting is null
            || setting.Value.RootElement.ValueKind != JsonValueKind.Object
            || !setting.Value.RootElement.TryGetProperty("hit", out var hit)
            || hit.ValueKind != JsonValueKind.String)
        {
            return SensitiveWordActions.Manual;
        }

        var value = hit.GetString();
        return value == SensitiveWordActions.Reject
            ? SensitiveWordActions.Reject
            : SensitiveWordActions.Manual;
    }

    private static bool TryNormalizeTerm(string? term, out string normalized)
    {
        normalized = (term ?? string.Empty).Trim();
        return normalized.Length is >= 1 and <= 100
            && !normalized.Any(char.IsControl);
    }

    private static bool TryNormalizeAction(string? action, out string normalized)
    {
        normalized = (action ?? string.Empty).Trim();
        return normalized is SensitiveWordActions.Manual or SensitiveWordActions.Reject;
    }

    private static AuditLog NewAudit(
        AuditContext audit,
        string action,
        Guid wordId,
        object metadata,
        DateTimeOffset createdAt)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = audit.ActorId,
            Action = action,
            TargetType = "sensitive_word",
            TargetId = wordId,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(metadata),
            CreatedAt = createdAt
        };
    }
}