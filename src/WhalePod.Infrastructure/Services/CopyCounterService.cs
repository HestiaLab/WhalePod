using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using WhalePod.Domain;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class CopyCounterService(
    WhalePodDbContext db,
    IOptions<CopyCounterOptions> options,
    TimeProvider timeProvider)
{
    public async Task<bool> RecordCopyAsync(Guid artifactId, string? clientIp, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.DedupeSecret))
        {
            throw new InvalidOperationException("CopyCounter:DedupeSecret is required.");
        }

        var now = timeProvider.GetUtcNow();
        var bucket = now.ToUnixTimeSeconds() / Math.Max(1, settings.BucketSeconds);
        var material = Encoding.UTF8.GetBytes($"{clientIp ?? "unknown"}|{artifactId:N}|{bucket}");
        var keyHash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.DedupeSecret), material);

        var target = await (
            from artifact in db.Artifacts
            join post in db.Posts on artifact.PostId equals post.Id
            where artifact.Id == artifactId
                && post.ModerationStatus == ModerationStatuses.Approved
                && post.DeletedAt == null
            select new { artifact.Id, PostId = post.Id })
            .SingleOrDefaultAsync(cancellationToken);

        if (target is null)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.CopyDedupes.Add(new CopyDedupe
        {
            KeyHash = keyHash,
            ExpiresAt = now.AddSeconds(Math.Max(60, settings.DedupeTtlSeconds))
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return false;
        }

        await db.Artifacts
            .Where(artifact => artifact.Id == artifactId)
            .ExecuteUpdateAsync(update => update.SetProperty(artifact => artifact.CopyCount, artifact => artifact.CopyCount + 1), cancellationToken);

        await db.Posts
            .Where(post => post.Id == target.PostId)
            .ExecuteUpdateAsync(update => update.SetProperty(post => post.CopyCount, post => post.CopyCount + 1), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return true;
    }
}