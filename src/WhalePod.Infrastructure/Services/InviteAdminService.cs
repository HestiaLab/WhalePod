using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum InviteAdminOutcome
{
    Success,
    Forbidden,
    InvalidInput,
    NoSelection
}

public sealed record InviteAdminItem(
    Guid Id,
    string? CodeHint,
    string CreatedByHandle,
    int MaxUses,
    int UsedCount,
    string Status,
    string State,
    string? Note,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    IReadOnlyList<string> RecentInvitees);

public sealed record InviteAdminList(
    IReadOnlyList<InviteAdminItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record InviteRedemptionItem(
    Guid Id,
    string UserHandle,
    string InvitedByHandle,
    DateTimeOffset CreatedAt);

public sealed record InviteAdminDetail(
    InviteAdminItem Invite,
    IReadOnlyList<InviteRedemptionItem> Redemptions,
    int Page,
    int PageSize,
    int TotalRedemptions);

public sealed record InviteCreationData(
    Guid Id,
    string? CodeHint,
    string PlaintextCode,
    string CreatedByHandle,
    int MaxUses,
    int UsedCount,
    string Status,
    string State,
    string? Note,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record InviteCreateResult(InviteAdminOutcome Outcome, InviteCreationData? Invite);

public sealed record InviteDisableResult(InviteAdminOutcome Outcome, int Requested, int Disabled, int Skipped);

public sealed class InviteAdminService(
    WhalePodDbContext db,
    AdminAccessService adminAccess,
    TimeProvider timeProvider)
{
    public const int PageSize = 20;
    public const int MaxBatchSize = 100;

    public async Task<InviteAdminList> ListAsync(
        Guid actorId,
        string? state,
        string? hint,
        string? member,
        int page,
        int pageSize = PageSize,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(actorId, cancellationToken))
        {
            throw ApiProblemException.Forbidden();
        }

        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var now = timeProvider.GetUtcNow();
        var query = db.InviteCodes.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(hint))
        {
            var normalizedHint = hint.Trim().TrimStart('…').ToUpperInvariant();
            if (normalizedHint.Length is < 1 or > 8)
            {
                return new InviteAdminList([], page, PageSize, 0);
            }

            query = query.Where(invite =>
                invite.CodeHint != null && invite.CodeHint.ToUpper().EndsWith(normalizedHint));
        }

        if (!string.IsNullOrWhiteSpace(member))
        {
            var handle = member.Trim().ToLowerInvariant();
            var memberIds = db.Users
                .AsNoTracking()
                .Where(user => user.Handle == handle)
                .Select(user => user.Id);

            query = query.Where(invite =>
                memberIds.Contains(invite.CreatedBy)
                || db.InviteRedemptions.Any(redemption =>
                    redemption.InviteCodeId == invite.Id && memberIds.Contains(redemption.UserId)));
        }

        query = ApplyStateFilter(query, state, now);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await (
                from invite in query
                join creator in db.Users.AsNoTracking() on invite.CreatedBy equals creator.Id
                orderby invite.CreatedAt descending, invite.Id descending
                select new { Invite = invite, CreatorHandle = creator.Handle })
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var inviteIds = rows.Select(row => row.Invite.Id).ToList();
        var redemptionRows = await (
                from redemption in db.InviteRedemptions.AsNoTracking()
                join invitee in db.Users.AsNoTracking() on redemption.UserId equals invitee.Id
                where inviteIds.Contains(redemption.InviteCodeId)
                orderby redemption.CreatedAt descending, redemption.Id descending
                select new { redemption.InviteCodeId, InviteeHandle = invitee.Handle })
            .ToListAsync(cancellationToken);

        var recentInvitees = redemptionRows
            .GroupBy(row => row.InviteCodeId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Take(3).Select(row => row.InviteeHandle).ToList());

        var items = rows
            .Select(row => ToItem(
                row.Invite,
                row.CreatorHandle,
                now,
                recentInvitees.GetValueOrDefault(row.Invite.Id, Array.Empty<string>())))
            .ToList();

        return new InviteAdminList(items, page, pageSize, totalCount);
    }

    public async Task<InviteAdminDetail?> GetDetailAsync(
        Guid inviteId,
        int page,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 100_000);
        var now = timeProvider.GetUtcNow();

        var row = await (
                from invite in db.InviteCodes.AsNoTracking()
                join creator in db.Users.AsNoTracking() on invite.CreatedBy equals creator.Id
                where invite.Id == inviteId
                select new { Invite = invite, CreatorHandle = creator.Handle })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var totalRedemptions = await db.InviteRedemptions
            .AsNoTracking()
            .CountAsync(redemption => redemption.InviteCodeId == inviteId, cancellationToken);

        var redemptionRows = await (
                from redemption in db.InviteRedemptions.AsNoTracking()
                join invitee in db.Users.AsNoTracking() on redemption.UserId equals invitee.Id
                join inviter in db.Users.AsNoTracking() on redemption.InvitedBy equals inviter.Id
                where redemption.InviteCodeId == inviteId
                orderby redemption.CreatedAt descending, redemption.Id descending
                select new
                {
                    redemption.Id,
                    UserHandle = invitee.Handle,
                    InvitedByHandle = inviter.Handle,
                    redemption.CreatedAt
                })
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        var redemptions = redemptionRows
            .Select(item => new InviteRedemptionItem(
                item.Id,
                item.UserHandle,
                item.InvitedByHandle,
                item.CreatedAt))
            .ToList();

        return new InviteAdminDetail(
            ToItem(row.Invite, row.CreatorHandle, now, Array.Empty<string>()),
            redemptions,
            page,
            PageSize,
            totalRedemptions);
    }

    public async Task<Guid?> FindInviteIdByCodeAsync(
        string? code,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = (code ?? string.Empty).Trim();
        if (normalizedCode.Length is < 1 or > 200)
        {
            return null;
        }

        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode));
        return await db.InviteCodes
            .AsNoTracking()
            .Where(invite => invite.CodeHash == codeHash)
            .Select(invite => (Guid?)invite.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<InviteCreateResult> CreateAsync(
        string? note,
        int maxUses,
        DateTimeOffset? expiresAt,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var actorHandle = await GetAdminHandleAsync(audit.ActorId, cancellationToken);
        if (actorHandle is null)
        {
            return new InviteCreateResult(InviteAdminOutcome.Forbidden, null);
        }

        if (maxUses is < 1 or > 1000 || !TryNormalizeNote(note, out var normalizedNote))
        {
            return new InviteCreateResult(InviteAdminOutcome.InvalidInput, null);
        }

        var now = timeProvider.GetUtcNow();
        if (expiresAt is not null && expiresAt <= now)
        {
            return new InviteCreateResult(InviteAdminOutcome.InvalidInput, null);
        }

        expiresAt = expiresAt?.ToUniversalTime();

        var (plaintextCode, codeHint) = GenerateCode();
        var invite = new InviteCode
        {
            CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintextCode)),
            CodeHint = codeHint,
            CreatedBy = audit.ActorId,
            MaxUses = maxUses,
            UsedCount = 0,
            ExpiresAt = expiresAt,
            Status = InviteCodeStatuses.Active,
            Note = normalizedNote,
            CreatedAt = now
        };

        db.InviteCodes.Add(invite);
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = audit.ActorId,
            Action = "invite.created",
            TargetType = "invite_code",
            TargetId = invite.Id,
            RequestId = audit.RequestId,
            IpHash = audit.IpHash,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                codeHint,
                maxUses,
                expiresAt
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        return new InviteCreateResult(
            InviteAdminOutcome.Success,
            new InviteCreationData(
                invite.Id,
                invite.CodeHint,
                plaintextCode,
                actorHandle,
                invite.MaxUses,
                invite.UsedCount,
                invite.Status,
                "active",
                invite.Note,
                invite.ExpiresAt,
                invite.CreatedAt));
    }

    public async Task<InviteDisableResult> DisableUnusedAsync(
        IReadOnlyCollection<Guid> inviteIds,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(audit.ActorId, cancellationToken))
        {
            return new InviteDisableResult(InviteAdminOutcome.Forbidden, 0, 0, 0);
        }

        var ids = inviteIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new InviteDisableResult(InviteAdminOutcome.NoSelection, 0, 0, 0);
        }

        if (ids.Count > MaxBatchSize)
        {
            return new InviteDisableResult(InviteAdminOutcome.InvalidInput, ids.Count, 0, ids.Count);
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var disabledIds = new List<Guid>();
        foreach (var inviteId in ids)
        {
            var affected = await db.InviteCodes
                .Where(invite => invite.Id == inviteId
                    && invite.Status == InviteCodeStatuses.Active
                    && invite.UsedCount == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(invite => invite.Status, InviteCodeStatuses.Disabled)
                        .SetProperty(invite => invite.DisabledAt, (DateTimeOffset?)now),
                    cancellationToken);

            if (affected > 0)
            {
                disabledIds.Add(inviteId);
            }
        }

        if (disabledIds.Count > 0)
        {
            db.AuditLogs.Add(new AuditLog
            {
                ActorType = AuditActorTypes.User,
                ActorId = audit.ActorId,
                Action = "invite.bulk_disabled",
                TargetType = "invite_code",
                RequestId = audit.RequestId,
                IpHash = audit.IpHash,
                MetadataJson = JsonSerializer.SerializeToDocument(new
                {
                    requested = ids.Count,
                    disabled = disabledIds.Count,
                    skipped = ids.Count - disabledIds.Count,
                    inviteIds = disabledIds
                }),
                CreatedAt = now
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new InviteDisableResult(
            InviteAdminOutcome.Success,
            ids.Count,
            disabledIds.Count,
            ids.Count - disabledIds.Count);
    }

    private async Task<string?> GetAdminHandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await adminAccess.IsAdminAsync(userId, cancellationToken))
        {
            return null;
        }

        return await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Handle)
            .SingleAsync(cancellationToken);
    }

    private static IQueryable<InviteCode> ApplyStateFilter(
        IQueryable<InviteCode> query,
        string? state,
        DateTimeOffset now)
    {
        return state switch
        {
            "disabled" => query.Where(invite => invite.Status == InviteCodeStatuses.Disabled),
            "exhausted" => query.Where(invite =>
                invite.Status == InviteCodeStatuses.Active && invite.UsedCount >= invite.MaxUses),
            "expired" => query.Where(invite =>
                invite.Status == InviteCodeStatuses.Active
                && invite.UsedCount < invite.MaxUses
                && invite.ExpiresAt != null
                && invite.ExpiresAt <= now),
            "active" => query.Where(invite =>
                invite.Status == InviteCodeStatuses.Active
                && invite.UsedCount < invite.MaxUses
                && (invite.ExpiresAt == null || invite.ExpiresAt > now)),
            _ => query
        };
    }

    private static InviteAdminItem ToItem(
        InviteCode invite,
        string creatorHandle,
        DateTimeOffset now,
        IReadOnlyList<string> recentInvitees)
    {
        return new InviteAdminItem(
            invite.Id,
            invite.CodeHint,
            creatorHandle,
            invite.MaxUses,
            invite.UsedCount,
            invite.Status,
            DeriveState(invite, now),
            invite.Note,
            invite.ExpiresAt,
            invite.CreatedAt,
            invite.DisabledAt,
            recentInvitees);
    }

    private static string DeriveState(InviteCode invite, DateTimeOffset now)
    {
        if (invite.Status == InviteCodeStatuses.Disabled)
        {
            return "disabled";
        }

        if (invite.UsedCount >= invite.MaxUses)
        {
            return "exhausted";
        }

        if (invite.ExpiresAt is not null && invite.ExpiresAt <= now)
        {
            return "expired";
        }

        return "active";
    }

    private static bool TryNormalizeNote(string? note, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalized is null)
        {
            return true;
        }

        if (normalized.EnumerateRunes().Count() > 200)
        {
            return false;
        }

        return normalized.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');
    }

    private static (string PlaintextCode, string CodeHint) GenerateCode()
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var plaintextCode = $"WP-{hex[..6]}-{hex[6..12]}-{hex[12..18]}-{hex[18..24]}";
        return (plaintextCode, $"…{plaintextCode[^4..]}");
    }
}