using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public enum InviteRedemptionOutcome
{
    Success,
    Invalid,
    Expired,
    Exhausted,
    AlreadyActivated,
    AccountUnavailable
}

public sealed class InviteRedemptionService(WhalePodDbContext db)
{
    public async Task<InviteRedemptionOutcome> RedeemAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var normalized = (code ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return InviteRedemptionOutcome.Invalid;
        }

        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM \"user\" WHERE id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return InviteRedemptionOutcome.Invalid;
        }

        if (user.Status != UserStatuses.Active)
        {
            return await FailAsync(transaction, user, InviteRedemptionOutcome.AccountUnavailable, null, cancellationToken);
        }

        if (user.ActivatedAt is not null)
        {
            return await FailAsync(transaction, user, InviteRedemptionOutcome.AlreadyActivated, null, cancellationToken);
        }

        var invite = await db.InviteCodes
            .FromSqlInterpolated($"SELECT * FROM invite_code WHERE code_hash = {codeHash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (invite is null || invite.Status != InviteCodeStatuses.Active)
        {
            return await FailAsync(transaction, user, InviteRedemptionOutcome.Invalid, invite, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        if (invite.ExpiresAt is not null && invite.ExpiresAt <= now)
        {
            return await FailAsync(transaction, user, InviteRedemptionOutcome.Expired, invite, cancellationToken);
        }

        if (invite.UsedCount >= invite.MaxUses)
        {
            return await FailAsync(transaction, user, InviteRedemptionOutcome.Exhausted, invite, cancellationToken);
        }

        invite.UsedCount++;
        user.ActivatedAt = now;
        user.UpdatedAt = now;

        db.InviteRedemptions.Add(new InviteRedemption
        {
            InviteCodeId = invite.Id,
            UserId = user.Id,
            InvitedBy = invite.CreatedBy,
            CreatedAt = now
        });

        db.AuditLogs.Add(NewAudit(user.Id, "invite.redeem_succeeded", invite.Id, InviteRedemptionOutcome.Success, now));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return InviteRedemptionOutcome.Success;
    }

    private async Task<InviteRedemptionOutcome> FailAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        User user,
        InviteRedemptionOutcome outcome,
        InviteCode? invite,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        db.ChangeTracker.Clear();

        db.AuditLogs.Add(NewAudit(user.Id, "invite.redeem_failed", invite?.Id, outcome, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    private static AuditLog NewAudit(Guid userId, string action, Guid? inviteCodeId, InviteRedemptionOutcome outcome, DateTimeOffset createdAt)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = userId,
            Action = action,
            TargetType = "user",
            TargetId = userId,
            MetadataJson = JsonSerializer.SerializeToDocument(new
            {
                outcome = outcome.ToString(),
                inviteCodeId
            }),
            CreatedAt = createdAt
        };
    }
}