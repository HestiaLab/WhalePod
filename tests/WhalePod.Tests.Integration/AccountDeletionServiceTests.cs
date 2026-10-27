using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class AccountDeletionServiceTests
{
    [DatabaseFact]
    public async Task Deletion_anonymizes_account_clears_identity_and_retains_content()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("delete-member");
        member.Email = $"delete-{ServiceTestSupport.NewSuffix()}@example.test";
        member.GitHubId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        member.Bio = "待删除的个人简介";
        member.AvatarUrl = "https://example.test/avatar.png";
        var other = ServiceTestSupport.NewUser("delete-other");
        var category = ServiceTestSupport.NewCategory();
        var ownPost = ServiceTestSupport.NewPost(member.Id, category.Id);
        var otherPost = ServiceTestSupport.NewPost(other.Id, category.Id);
        var acceptedComment = ServiceTestSupport.NewComment(
            otherPost.Id,
            member.Id,
            body: "被采纳的评论，账号注销后匿名保留");
        acceptedComment.IsAccepted = true;
        otherPost.AcceptedCommentId = acceptedComment.Id;
        otherPost.IsSolved = true;
        db.Users.AddRange(member, other);
        db.Categories.Add(category);
        db.Posts.AddRange(ownPost, otherPost);
        db.Comments.Add(acceptedComment);
        db.ExternalLogins.Add(new ExternalLogin
        {
            UserId = member.Id,
            ProviderUserId = $"github-{ServiceTestSupport.NewSuffix()}",
            ProviderLogin = member.Handle
        });
        db.Devices.Add(new Device
        {
            UserId = member.Id,
            PublicKey = $"public-key-{ServiceTestSupport.NewSuffix()}",
            Fingerprint = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            KernelVersion = "6.8",
            Platform = Platforms.LinuxX64
        });
        db.EmailPreferences.Add(new EmailPreference
        {
            UserId = member.Id,
            Type = EmailPreferenceTypes.RepliesMentions,
            Enabled = false,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var originalHandle = member.Handle;
        var originalEmail = member.Email;

        var service = NewService(db);
        var result = await service.DeleteAsync(
            member.Id,
            member.Id,
            false,
            originalHandle,
            ServiceTestSupport.NewAudit(member.Id));

        Assert.True(result.Deleted);
        Assert.False(result.ContentDeleted);
        db.ChangeTracker.Clear();
        var stored = await db.Users.SingleAsync(user => user.Id == member.Id);
        Assert.Equal(UserStatuses.Deleted, stored.Status);
        Assert.NotNull(stored.DeletedAt);
        Assert.StartsWith("deleted-", stored.Handle, StringComparison.Ordinal);
        Assert.NotEqual(originalHandle, stored.Handle);
        Assert.Equal("已注销用户", stored.DisplayName);
        Assert.Null(stored.Email);
        Assert.Null(stored.GitHubId);
        Assert.Null(stored.GitHubIdHmac);
        Assert.Null(stored.Bio);
        Assert.Null(stored.AvatarUrl);
        Assert.Equal(UserRoles.Member, stored.Role);
        Assert.Equal(0, await db.ExternalLogins.CountAsync(login => login.UserId == member.Id));
        Assert.Equal(0, await db.EmailPreferences.CountAsync(preference => preference.UserId == member.Id));
        var device = await db.Devices.SingleAsync(candidate => candidate.UserId == member.Id);
        Assert.Equal(DeviceStatuses.Revoked, device.Status);
        Assert.NotNull(device.RevokedAt);
        Assert.Equal("account_deleted", device.RevokeReason);

        var ownPostStored = await db.Posts.SingleAsync(post => post.Id == ownPost.Id);
        Assert.Equal(ModerationStatuses.Approved, ownPostStored.ModerationStatus);
        Assert.Null(ownPostStored.DeletedAt);
        var acceptedStored = await db.Comments.SingleAsync(comment => comment.Id == acceptedComment.Id);
        Assert.Equal(ModerationStatuses.Approved, acceptedStored.ModerationStatus);
        Assert.True(acceptedStored.IsAccepted);
        var otherPostStored = await db.Posts.SingleAsync(post => post.Id == otherPost.Id);
        Assert.True(otherPostStored.IsSolved);
        Assert.Equal(acceptedComment.Id, otherPostStored.AcceptedCommentId);
        Assert.Equal(0, await db.Comments.CountAsync(comment =>
            comment.AuthorId == member.Id && comment.DeletedAt != null));

        var email = await db.EmailOutbox.SingleAsync(outbox =>
            outbox.UserId == member.Id && outbox.Template == "account_deleted");
        Assert.Equal(originalEmail, email.ToAddress);
        Assert.Equal(EmailStatuses.Pending, email.Status);
        Assert.True(await db.AuditLogs.AnyAsync(log =>
            log.Action == "account.deleted" && log.TargetId == member.Id));
    }

    [DatabaseFact]
    public async Task Deletion_can_remove_authored_content_and_clear_accepted_answer()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("delete-content-member");
        var other = ServiceTestSupport.NewUser("delete-content-other");
        var category = ServiceTestSupport.NewCategory();
        var ownPost = ServiceTestSupport.NewPost(member.Id, category.Id);
        var otherPost = ServiceTestSupport.NewPost(other.Id, category.Id);
        var acceptedComment = ServiceTestSupport.NewComment(
            otherPost.Id,
            member.Id,
            body: "删除账号时同时删除的采纳评论");
        acceptedComment.IsAccepted = true;
        otherPost.AcceptedCommentId = acceptedComment.Id;
        otherPost.IsSolved = true;
        db.Users.AddRange(member, other);
        db.Categories.Add(category);
        db.Posts.AddRange(ownPost, otherPost);
        db.Comments.Add(acceptedComment);
        await db.SaveChangesAsync();
        var expectedCommentVersion = acceptedComment.Version + 1;

        var service = NewService(db);
        var result = await service.DeleteAsync(
            member.Id,
            member.Id,
            true,
            member.Handle,
            ServiceTestSupport.NewAudit(member.Id));

        Assert.True(result.ContentDeleted);
        Assert.Equal(1, result.PostsDeleted);
        Assert.Equal(1, result.CommentsDeleted);
        db.ChangeTracker.Clear();
        var storedPost = await db.Posts.SingleAsync(post => post.Id == ownPost.Id);
        Assert.Equal(ModerationStatuses.Deleted, storedPost.ModerationStatus);
        Assert.NotNull(storedPost.DeletedAt);
        var storedComment = await db.Comments.SingleAsync(comment => comment.Id == acceptedComment.Id);
        Assert.Equal(ModerationStatuses.Deleted, storedComment.ModerationStatus);
        Assert.False(storedComment.IsAccepted);
        Assert.Equal(expectedCommentVersion, storedComment.Version);
        var otherPostStored = await db.Posts.SingleAsync(post => post.Id == otherPost.Id);
        Assert.Null(otherPostStored.AcceptedCommentId);
        Assert.False(otherPostStored.IsSolved);
        Assert.True(otherPostStored.Version > 1);
    }

    [DatabaseFact]
    public async Task Deletion_requires_handle_confirmation_and_protects_last_admin()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var admin = ServiceTestSupport.NewUser("delete-last-admin", UserRoles.Admin);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var service = NewService(db);

        var wrong = await Assert.ThrowsAsync<ApiProblemException>(
            () => service.DeleteAsync(
                admin.Id,
                admin.Id,
                false,
                "wrong-handle",
                ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, wrong.Code);

        var otherAdmins = await db.Users
            .Where(user => user.Id != admin.Id
                && user.Role == UserRoles.Admin
                && user.Status == UserStatuses.Active
                && user.ActivatedAt != null
                && user.DeletedAt == null)
            .ToListAsync();
        foreach (var otherAdmin in otherAdmins)
        {
            otherAdmin.Role = UserRoles.Member;
        }

        await db.SaveChangesAsync();
        try
        {
            var lastAdmin = await Assert.ThrowsAsync<ApiProblemException>(
                () => service.DeleteAsync(
                    admin.Id,
                    admin.Id,
                    false,
                    admin.Handle,
                    ServiceTestSupport.NewAudit(admin.Id)));
            Assert.Equal(ApiErrorCodes.Forbidden, lastAdmin.Code);
            Assert.Equal(
                "last_admin",
                System.Text.Json.JsonSerializer.SerializeToElement(lastAdmin.Details).GetProperty("reason").GetString());
            Assert.False(await db.Users.AnyAsync(user =>
                user.Id == admin.Id && user.DeletedAt != null));
        }
        finally
        {
            foreach (var otherAdmin in otherAdmins)
            {
                otherAdmin.Role = UserRoles.Admin;
            }

            await db.SaveChangesAsync();
        }
    }

    [DatabaseFact]
    public async Task Admin_deletion_of_banned_user_respects_github_hmac_feature_gate()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var admin = ServiceTestSupport.NewUser("delete-hmac-admin", UserRoles.Admin);
        var banned = ServiceTestSupport.NewUser(
            "delete-hmac-banned",
            status: UserStatuses.Banned,
            statusReason: "严重滥用");
        banned.GitHubId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        banned.Email = $"banned-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.AddRange(admin, banned);
        await db.SaveChangesAsync();

        var disabledService = NewService(db);
        var disabled = await disabledService.DeleteAsync(
            admin.Id,
            banned.Id,
            false,
            banned.Handle,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.False(disabled.GitHubHmacRetained);
        db.ChangeTracker.Clear();
        var deleted = await db.Users.SingleAsync(user => user.Id == banned.Id);
        Assert.Null(deleted.GitHubId);
        Assert.Null(deleted.GitHubIdHmac);

        var enabledTarget = ServiceTestSupport.NewUser(
            "delete-hmac-enabled",
            status: UserStatuses.Banned,
            statusReason: "严重滥用");
        var enabledGitHubId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        enabledTarget.GitHubId = enabledGitHubId;
        enabledTarget.Email = $"banned2-{ServiceTestSupport.NewSuffix()}@example.test";
        db.Users.Add(enabledTarget);
        await db.SaveChangesAsync();
        var secret = "test-only-github-hmac-secret-0123456789";
        var enabledService = NewService(
            db,
            new AccountSecurityOptions
            {
                GitHubHmacBlacklistEnabled = true,
                GitHubHmacSecret = secret
            });
        var enabled = await enabledService.DeleteAsync(
            admin.Id,
            enabledTarget.Id,
            false,
            enabledTarget.Handle,
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.True(enabled.GitHubHmacRetained);
        db.ChangeTracker.Clear();
        var stored = await db.Users.SingleAsync(user => user.Id == enabledTarget.Id);
        Assert.Null(stored.GitHubId);
        Assert.NotNull(stored.GitHubIdHmac);
        Assert.Equal(32, stored.GitHubIdHmac!.Length);
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(enabledGitHubId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(expected, stored.GitHubIdHmac);
    }

    private static AccountDeletionService NewService(
        WhalePod.Infrastructure.Persistence.WhalePodDbContext db,
        AccountSecurityOptions? security = null)
    {
        return new AccountDeletionService(
            db,
            new ActorGuard(db, TimeProvider.System),
            new EmailNotificationQueue(db),
            Options.Create(security ?? new AccountSecurityOptions()),
            TimeProvider.System);
    }
}