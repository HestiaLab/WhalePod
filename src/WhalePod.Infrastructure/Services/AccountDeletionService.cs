using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed class AccountSecurityOptions
{
    public bool GitHubHmacBlacklistEnabled { get; set; }
    public string? GitHubHmacSecret { get; set; }
}

public sealed record AccountDeletionResult(
    bool Deleted,
    bool ContentDeleted,
    int PostsDeleted,
    int CommentsDeleted,
    int DevicesRevoked,
    int ExternalLoginsRemoved,
    bool GitHubHmacRetained,
    int MediaDeleted);

public sealed class AccountDeletionService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    EmailNotificationQueue emailQueue,
    IOptions<AccountSecurityOptions> options,
    TimeProvider timeProvider)
{
    public async Task<AccountDeletionResult> DeleteAsync(
        Guid actorId,
        Guid targetUserId,
        bool deleteContent,
        string? confirmationHandle,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var isSelf = actorId == targetUserId;
        if (isSelf)
        {
            await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        }
        else
        {
            await actorGuard.RequireAdminAsync(actorId, cancellationToken);
        }

        var security = options.Value;
        if (security.GitHubHmacBlacklistEnabled
            && (string.IsNullOrWhiteSpace(security.GitHubHmacSecret)
                || security.GitHubHmacSecret.Length < 32))
        {
            throw new InvalidOperationException(
                "Account:GitHubHmacSecret must be at least 32 characters when the blacklist is enabled.");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7210521)", cancellationToken);
        var user = await db.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            throw ApiProblemException.NotFound("用户不存在。");
        }

        if (user.Status == UserStatuses.Deleted || user.DeletedAt is not null)
        {
            throw ApiProblemException.Conflict("账号已注销。");
        }

        if (!string.Equals(
                (confirmationHandle ?? string.Empty).Trim(),
                user.Handle,
                StringComparison.OrdinalIgnoreCase))
        {
            throw ApiProblemException.Validation("confirmationHandle", "请输入当前账号 handle 以确认注销。");
        }

        if (user.Role == UserRoles.Admin)
        {
            var otherAdmins = await db.Users.CountAsync(candidate =>
                candidate.Id != user.Id
                && candidate.Role == UserRoles.Admin
                && candidate.Status == UserStatuses.Active
                && candidate.ActivatedAt != null
                && candidate.DeletedAt == null, cancellationToken);
            if (otherAdmins == 0)
            {
                throw ApiProblemException.Forbidden("last_admin", "不能注销最后一个有效管理员账号。");
            }
        }

        var postsDeleted = 0;
        var commentsDeleted = 0;
        var mediaDeleted = 0;
        if (deleteContent)
        {
            var posts = await db.Posts
                .Where(post => post.AuthorId == user.Id)
                .ToListAsync(cancellationToken);
            var postIds = posts.Select(post => post.Id).ToList();
            var postImages = await db.PostImages
                .Where(image => postIds.Contains(image.PostId))
                .ToListAsync(cancellationToken);
            var uploadIds = postImages
                .Select(image => image.UploadId)
                .Distinct()
                .ToList();
            var uploads = await db.MediaUploads
                .Where(upload => uploadIds.Contains(upload.Id)
                    && upload.Status != MediaUploadStatuses.Deleted)
                .ToListAsync(cancellationToken);
            foreach (var upload in uploads)
            {
                upload.Status = MediaUploadStatuses.Deleted;
                upload.ExpiresAt = now;
                upload.PostId = null;
            }

            mediaDeleted += uploads.Count;
            db.PostImages.RemoveRange(postImages);
            foreach (var post in posts.Where(post => post.DeletedAt is null))
            {
                post.ModerationStatus = ModerationStatuses.Deleted;
                post.DeletedAt = now;
                post.Version++;
                post.UpdatedAt = now;
                postsDeleted++;
            }

            var comments = await db.Comments
                .Where(comment => comment.AuthorId == user.Id && comment.DeletedAt == null)
                .ToListAsync(cancellationToken);
            var acceptedPostIds = comments
                .Where(comment => comment.IsAccepted)
                .Select(comment => comment.PostId)
                .Distinct()
                .ToList();
            var acceptedPosts = await db.Posts
                .Where(post => acceptedPostIds.Contains(post.Id))
                .ToDictionaryAsync(post => post.Id, cancellationToken);
            foreach (var comment in comments)
            {
                if (comment.IsAccepted
                    && acceptedPosts.TryGetValue(comment.PostId, out var post)
                    && post.AcceptedCommentId == comment.Id)
                {
                    post.AcceptedCommentId = null;
                    post.IsSolved = false;
                    post.Version++;
                    post.UpdatedAt = now;
                }

                comment.IsAccepted = false;
                comment.ModerationStatus = ModerationStatuses.Deleted;
                comment.DeletedAt = now;
                comment.Version++;
                comment.UpdatedAt = now;
                commentsDeleted++;
            }
        }

        var devices = await db.Devices
            .Where(device => device.UserId == user.Id && device.Status == DeviceStatuses.Active)
            .ToListAsync(cancellationToken);
        foreach (var device in devices)
        {
            device.Status = DeviceStatuses.Revoked;
            device.RevokedAt = now;
            device.RevokedBy = actorId;
            device.RevokeReason = "account_deleted";
        }

        var externalLogins = await db.ExternalLogins
            .Where(login => login.UserId == user.Id)
            .ToListAsync(cancellationToken);
        db.ExternalLogins.RemoveRange(externalLogins);
        var emailPreferences = await db.EmailPreferences
            .Where(preference => preference.UserId == user.Id)
            .ToListAsync(cancellationToken);
        db.EmailPreferences.RemoveRange(emailPreferences);
        var avatars = await db.MediaUploads
            .Where(upload => upload.OwnerId == user.Id
                && upload.Status == MediaUploadStatuses.Avatar)
            .ToListAsync(cancellationToken);
        foreach (var avatar in avatars)
        {
            avatar.Status = MediaUploadStatuses.Deleted;
            avatar.ExpiresAt = now;
        }

        mediaDeleted += avatars.Count;

        emailQueue.EnqueueAccountDeleted(user.Id, user.Email, now);

        var retainGitHubHmac = security.GitHubHmacBlacklistEnabled
            && user.Status == UserStatuses.Banned
            && user.GitHubId is not null;
        var githubHmac = retainGitHubHmac
            ? HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(security.GitHubHmacSecret!),
                Encoding.UTF8.GetBytes(
                    user.GitHubId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            : null;

        user.Handle = $"deleted-{Convert.ToHexString(SHA256.HashData(user.Id.ToByteArray()))[..24]}".ToLowerInvariant();
        user.DisplayName = "已注销用户";
        user.Bio = null;
        user.AvatarUrl = null;
        user.GitHubAvatarUrl = null;
        user.Email = null;
        user.EmailVerified = false;
        user.GitHubId = null;
        user.GitHubIdHmac = githubHmac;
        user.Role = UserRoles.Member;
        user.Status = UserStatuses.Deleted;
        user.StatusReason = null;
        user.StatusExpiresAt = null;
        user.DeletedAt = now;
        user.LastLoginAt = null;
        user.UpdatedAt = now;

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "account.deleted",
            "user",
            user.Id,
            new
            {
                contentPolicy = deleteContent ? "delete" : "anonymize",
                postsDeleted,
                commentsDeleted,
                devicesRevoked = devices.Count,
                externalLoginsRemoved = externalLogins.Count,
                githubHmacRetained = retainGitHubHmac,
                mediaDeleted
            },
            now));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AccountDeletionResult(
            true,
            deleteContent,
            postsDeleted,
            commentsDeleted,
            devices.Count,
            externalLogins.Count,
            retainGitHubHmac,
            mediaDeleted);
    }
}