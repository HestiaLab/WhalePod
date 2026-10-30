using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Communications;
using WhalePod.Domain.Content;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;

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

    [DatabaseFact]
    public async Task Deletion_with_content_marks_post_images_deleted_and_cleanup_removes_objects()
    {
        await using var db = ServiceTestSupport.CreateContext();
        await db.Database.MigrateAsync();

        var member = ServiceTestSupport.NewUser("delete-media-member");
        var category = ServiceTestSupport.NewCategory();
        var post = ServiceTestSupport.NewPost(member.Id, category.Id);
        var alreadyDeletedPost = ServiceTestSupport.NewPost(member.Id, category.Id);
        alreadyDeletedPost.ModerationStatus = ModerationStatuses.Deleted;
        alreadyDeletedPost.DeletedAt = DateTimeOffset.UtcNow.AddHours(-1);
        using var media = new TestMediaStorage();
        var url = await media.Storage.SaveAsync(
            TestImages.Png(8, 6),
            $"2026/10/{Guid.NewGuid():N}.webp",
            "image/webp");
        var upload = new MediaUpload
        {
            OwnerId = member.Id,
            Url = url,
            Width = 8,
            Height = 6,
            MimeType = "image/webp",
            ByteSize = 3,
            Status = MediaUploadStatuses.Attached,
            PostId = post.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            AttachedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };
        var postImage = new PostImage
        {
            PostId = post.Id,
            UploadId = upload.Id,
            Url = url,
            Width = 8,
            Height = 6,
            MimeType = "image/webp",
            ByteSize = 3,
            SortOrder = 0
        };
        var deletedUrl = await media.Storage.SaveAsync(
            TestImages.Png(5, 4),
            $"2026/10/{Guid.NewGuid():N}.webp",
            "image/webp");
        var deletedUpload = new MediaUpload
        {
            OwnerId = member.Id,
            Url = deletedUrl,
            Width = 5,
            Height = 4,
            MimeType = "image/webp",
            ByteSize = 3,
            Status = MediaUploadStatuses.Attached,
            PostId = alreadyDeletedPost.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            AttachedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };
        var deletedPostImage = new PostImage
        {
            PostId = alreadyDeletedPost.Id,
            UploadId = deletedUpload.Id,
            Url = deletedUrl,
            Width = 5,
            Height = 4,
            MimeType = "image/webp",
            ByteSize = 3,
            SortOrder = 0
        };
        db.Users.Add(member);
        db.Categories.Add(category);
        db.Posts.AddRange(post, alreadyDeletedPost);
        db.MediaUploads.AddRange(upload, deletedUpload);
        db.PostImages.AddRange(postImage, deletedPostImage);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var result = await service.DeleteAsync(
            member.Id,
            member.Id,
            true,
            member.Handle,
            ServiceTestSupport.NewAudit(member.Id));
        Assert.Equal(1, result.PostsDeleted);
        Assert.Equal(2, result.MediaDeleted);

        db.ChangeTracker.Clear();
        var storedUpload = await db.MediaUploads.AsNoTracking().SingleAsync(candidate => candidate.Id == upload.Id);
        Assert.Equal(MediaUploadStatuses.Deleted, storedUpload.Status);
        Assert.Null(storedUpload.PostId);
        var storedDeletedUpload = await db.MediaUploads.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deletedUpload.Id);
        Assert.Equal(MediaUploadStatuses.Deleted, storedDeletedUpload.Status);
        Assert.Null(await db.PostImages.AsNoTracking()
            .SingleOrDefaultAsync(image => image.Id == postImage.Id));
        Assert.Null(await db.PostImages.AsNoTracking()
            .SingleOrDefaultAsync(image => image.Id == deletedPostImage.Id));

        var cleanup = ServiceTestSupport.CreateMediaCleanupService(db, media.Storage);
        var cleanupResult = await cleanup.CleanupOnceAsync(100);
        Assert.True(cleanupResult.Deleted >= 2);
        Assert.False(File.Exists(media.ResolvePath(url)));
        Assert.False(File.Exists(media.ResolvePath(deletedUrl)));
        db.ChangeTracker.Clear();
        Assert.Null(await db.MediaUploads.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == upload.Id));
        Assert.Null(await db.MediaUploads.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == deletedUpload.Id));

        await cleanup.CleanupOnceAsync(100);
        db.ChangeTracker.Clear();
        Assert.Null(await db.MediaUploads.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == upload.Id));
        Assert.Null(await db.MediaUploads.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == deletedUpload.Id));
    }

    [DatabaseFact]
    public async Task Avatar_upload_racing_account_deletion_does_not_write_back_or_leave_objects()
    {
        await using var uploadDb = ServiceTestSupport.CreateContext();
        await uploadDb.Database.MigrateAsync();
        var member = ServiceTestSupport.NewUser("avatar-race-member");
        uploadDb.Users.Add(member);
        await uploadDb.SaveChangesAsync();

        using var media = new TestMediaStorage();
        var storage = new BlockingMediaStorage(media.Storage);
        var uploadService = new ImageUploadService(
            uploadDb,
            new ActorGuard(uploadDb, TimeProvider.System),
            storage,
            TimeProvider.System,
            NullLogger<ImageUploadService>.Instance);
        var avatarBytes = TestImages.Png(16, 12);
        var uploadTask = uploadService.UploadAvatarAsync(
            member.Id,
            new MemoryStream(avatarBytes),
            avatarBytes.Length,
            ServiceTestSupport.NewAudit(member.Id));
        var savedUrl = await storage.Saved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var deletionDb = ServiceTestSupport.CreateContext();
        var deletionService = new AccountDeletionService(
            deletionDb,
            new ActorGuard(deletionDb, TimeProvider.System),
            new EmailNotificationQueue(deletionDb),
            Options.Create(new AccountSecurityOptions()),
            TimeProvider.System);
        var deleted = await deletionService.DeleteAsync(
            member.Id,
            member.Id,
            false,
            member.Handle,
            ServiceTestSupport.NewAudit(member.Id));
        Assert.True(deleted.Deleted);

        storage.Release.TrySetResult();
        var conflict = await Assert.ThrowsAsync<ApiProblemException>(() => uploadTask);
        Assert.Equal(ApiErrorCodes.Conflict, conflict.Code);
        Assert.Contains(savedUrl, storage.DeletedUrls);

        deletionDb.ChangeTracker.Clear();
        var storedUser = await deletionDb.Users.AsNoTracking().SingleAsync(user => user.Id == member.Id);
        Assert.Equal(UserStatuses.Deleted, storedUser.Status);
        Assert.Null(storedUser.AvatarUrl);
        Assert.False(await deletionDb.MediaUploads.AsNoTracking().AnyAsync(upload =>
            upload.OwnerId == member.Id && upload.Status == MediaUploadStatuses.Avatar));
        Assert.False(File.Exists(media.ResolvePath(savedUrl)));
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

    private sealed class BlockingMediaStorage(IMediaStorage inner) : IMediaStorage
    {
        public TaskCompletionSource<string> Saved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentBag<string> DeletedUrls { get; } = [];

        public string ProviderName => inner.ProviderName;

        public async Task<string> SaveAsync(
            ReadOnlyMemory<byte> content,
            string objectKey,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            var url = await inner.SaveAsync(content, objectKey, contentType, cancellationToken);
            Saved.TrySetResult(url);
            await Release.Task.WaitAsync(cancellationToken);
            return url;
        }

        public Task DeleteAsync(string publicUrl, CancellationToken cancellationToken = default)
        {
            DeletedUrls.Add(publicUrl);
            return inner.DeleteAsync(publicUrl, cancellationToken);
        }
    }
}