using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WhalePod.Contracts.Devices;
using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Domain.Platform;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Integration;

public sealed class DeviceVerificationServiceTests
{
    [DatabaseFact]
    public async Task Admin_device_history_is_admin_only_bounded_and_excludes_sensitive_metadata()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var suffix = ServiceTestSupport.NewSuffix();
        var admin = await CreateUserAsync(db, "device-history-admin", UserRoles.Admin);
        var target = await CreateUserAsync(db, "device-history-target");
        var member = await CreateUserAsync(db, "device-history-member");
        var now = DateTimeOffset.UtcNow;

        var activeDevice = NewDevice(target.Id, now.AddDays(-2));
        var revokedDevice = NewDevice(target.Id, now.AddDays(-3));
        revokedDevice.Status = DeviceStatuses.Revoked;
        revokedDevice.RevokedAt = now.AddHours(-1);
        revokedDevice.RevokedBy = admin.Id;
        revokedDevice.RevokeReason = "管理员测试撤销";
        db.Devices.AddRange(activeDevice, revokedDevice);
        db.AuditLogs.AddRange(
            NewAudit(target.Id, "device.challenge_created", "device_challenge", Guid.NewGuid(), new { protocolVersion = "community-device-v1" }, now.AddMinutes(-5)),
            NewAudit(target.Id, "device.verified", "device", activeDevice.Id, new { platform = "linux-x64" }, now.AddMinutes(-4)),
            NewAudit(target.Id, "device.verification_failed", "device_challenge", Guid.NewGuid(), new
            {
                reason = "signature_invalid",
                code = "SIGNATURE_INVALID",
                email = "secret-device@example.test",
                ipHash = "198.51.100.7"
            }, now.AddMinutes(-3)),
            new AuditLog
            {
                ActorType = AuditActorTypes.User,
                ActorId = admin.Id,
                Action = "device.revoked_by_admin",
                TargetType = "device",
                TargetId = revokedDevice.Id,
                MetadataJson = JsonSerializer.SerializeToDocument(new
                {
                    reason = revokedDevice.RevokeReason,
                    fingerprint = revokedDevice.Fingerprint,
                    ownerUserId = target.Id,
                    ipHash = "203.0.113.9"
                }),
                CreatedAt = now.AddMinutes(-2)
            });
        for (var index = 0; index < AdminDeviceHistoryLimits.Events + 1; index++)
        {
            db.AuditLogs.Add(NewAudit(
                target.Id,
                "device.verified",
                "device",
                activeDevice.Id,
                new { platform = "linux-x64" },
                now.AddHours(-2).AddSeconds(index)));
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, new DeviceVerificationOptions());
        var history = await service.GetAdminHistoryAsync(admin.Id, target.Id);

        Assert.Equal(target.Handle, history.User.Handle);
        Assert.Equal(1, history.ActiveDeviceCount);
        Assert.Equal(2, history.Devices.Count);
        Assert.True(history.EventsTruncated);
        Assert.Equal(AdminDeviceHistoryLimits.Events, history.Events.Count);
        var revoked = history.Devices.Single(device => device.Id == revokedDevice.Id);
        Assert.Equal(admin.Handle, revoked.RevokedByLabel);
        Assert.Equal("管理员测试撤销", revoked.RevokeReason);
        var failed = history.Events.Single(item => item.Action == "device.verification_failed");
        Assert.NotNull(failed.Detail);
        Assert.Contains("signature_invalid", failed.Detail);
        Assert.Contains("SIGNATURE_INVALID", failed.Detail);
        var adminRevoke = history.Events.Single(item => item.Action == "device.revoked_by_admin");
        Assert.Equal(admin.Handle, adminRevoke.ActorLabel);
        Assert.NotNull(adminRevoke.Detail);
        Assert.Contains("管理员测试撤销", adminRevoke.Detail);
        var displayedText = string.Join(
            "|",
            history.Events.Select(item => $"{item.ActionLabel}|{item.ActorLabel}|{item.Detail}"));
        Assert.DoesNotContain("secret-device@example.test", displayedText, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100.7", displayedText, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", displayedText, StringComparison.Ordinal);

        var forbidden = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.GetAdminHistoryAsync(member.Id, target.Id));
        Assert.Equal(ApiErrorCodes.Forbidden, forbidden.Code);

        var missing = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.GetAdminHistoryAsync(admin.Id, Guid.NewGuid()));
        Assert.Equal(ApiErrorCodes.NotFound, missing.Code);
    }

    [DatabaseFact]
    public async Task Challenge_is_hashed_one_time_and_creates_device_badge()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var user = await CreateUserAsync(db, "device-owner");
        var service = CreateService(db, new DeviceVerificationOptions());
        var challenge = await service.CreateChallengeAsync(user.Id, ServiceTestSupport.NewAudit(user.Id));

        var stored = await db.DeviceChallenges
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);
        Assert.Equal(32, stored.ChallengeHash.Length);
        Assert.NotEqual(
            System.Text.Encoding.UTF8.GetBytes(challenge.Challenge),
            stored.ChallengeHash);
        Assert.True(stored.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(4));
        Assert.Contains(user.Handle, challenge.CliCommand, StringComparison.Ordinal);
        Assert.Equal(DeviceProtocol.Name, challenge.ProtocolVersion);
        Assert.Equal(
            1,
            await db.AuditLogs.CountAsync(log =>
                log.ActorId == user.Id && log.Action == "device.challenge_created"));

        var (_, request) = BuildRequest(challenge.Challenge, user.Handle);
        var result = await service.CompleteAsync(
            user.Id,
            request,
            ServiceTestSupport.NewAudit(user.Id));

        Assert.True(result.Badge.Verified);
        Assert.Equal(1, result.Badge.ActiveDeviceCount);
        Assert.NotEqual(Guid.Empty, result.Device.Id);
        db.ChangeTracker.Clear();
        var device = await db.Devices.SingleAsync(candidate => candidate.UserId == user.Id);
        Assert.Equal(DeviceStatuses.Active, device.Status);
        Assert.Equal(64, device.Fingerprint.Length);
        Assert.NotNull(
            (await db.DeviceChallenges.AsNoTracking().SingleAsync(candidate => candidate.Id == stored.Id)).UsedAt);
        Assert.Equal(
            1,
            await db.Notifications.CountAsync(notification =>
                notification.UserId == user.Id && notification.Type == NotificationTypes.DeviceVerified));
        Assert.Equal(
            1,
            await db.AuditLogs.CountAsync(log =>
                log.ActorId == user.Id && log.Action == "device.verified"));

        var usedAgain = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.CompleteAsync(user.Id, request, ServiceTestSupport.NewAudit(user.Id)));
        Assert.Equal(ApiErrorCodes.ChallengeUsed, usedAgain.Code);
        Assert.Equal(1, await db.Devices.CountAsync(candidate => candidate.UserId == user.Id));
    }

    [DatabaseFact]
    public async Task Failed_signature_does_not_consume_challenge_and_eventually_locks()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var user = await CreateUserAsync(db, "device-lock");
        var options = new DeviceVerificationOptions
        {
            MaxFailedAttempts = 2,
            LockoutSeconds = 300,
            FailureWindowSeconds = 600
        };
        var service = CreateService(db, options);
        var challenge = await service.CreateChallengeAsync(user.Id, ServiceTestSupport.NewAudit(user.Id));
        var (_, validRequest) = BuildRequest(challenge.Challenge, user.Handle);

        var invalid = validRequest with { Signature = Convert.ToBase64String(new byte[64]) };
        var first = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.CompleteAsync(user.Id, invalid, ServiceTestSupport.NewAudit(user.Id)));
        Assert.Equal(ApiErrorCodes.SignatureInvalid, first.Code);

        var second = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.CompleteAsync(user.Id, invalid, ServiceTestSupport.NewAudit(user.Id)));
        Assert.Equal(ApiErrorCodes.SignatureInvalid, second.Code);

        var stored = await db.DeviceChallenges
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);
        Assert.Null(stored.UsedAt);

        var locked = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.CompleteAsync(user.Id, validRequest, ServiceTestSupport.NewAudit(user.Id)));
        Assert.Equal(ApiErrorCodes.DeviceVerificationLocked, locked.Code);
        Assert.Equal(429, locked.StatusCode);
        Assert.True(locked.RetryAfterSeconds > 0);
        Assert.Equal(0, await db.Devices.CountAsync(candidate => candidate.UserId == user.Id));
    }

    [DatabaseFact]
    public async Task Expired_challenge_is_rejected()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var user = await CreateUserAsync(db, "device-expiry");
        var service = CreateService(db, new DeviceVerificationOptions());
        var challenge = await service.CreateChallengeAsync(user.Id, ServiceTestSupport.NewAudit(user.Id));
        var stored = await db.DeviceChallenges.SingleAsync(candidate => candidate.UserId == user.Id);
        stored.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        stored.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        var (_, request) = BuildRequest(challenge.Challenge, user.Handle);
        var exception = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.CompleteAsync(user.Id, request, ServiceTestSupport.NewAudit(user.Id)));
        Assert.Equal(ApiErrorCodes.ChallengeExpired, exception.Code);
        Assert.Equal(0, await db.Devices.CountAsync(candidate => candidate.UserId == user.Id));
    }

    [DatabaseFact]
    public async Task Device_limit_and_cross_account_public_key_are_rejected()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var firstUser = await CreateUserAsync(db, "device-limit-a");
        var secondUser = await CreateUserAsync(db, "device-limit-b");
        var service = CreateService(db, new DeviceVerificationOptions { MaxActiveDevices = 1 });

        var firstChallenge = await service.CreateChallengeAsync(
            firstUser.Id,
            ServiceTestSupport.NewAudit(firstUser.Id));
        var (firstKey, firstRequest) = BuildRequest(firstChallenge.Challenge, firstUser.Handle);
        await service.CompleteAsync(firstUser.Id, firstRequest, ServiceTestSupport.NewAudit(firstUser.Id));

        var secondChallenge = await service.CreateChallengeAsync(
            firstUser.Id,
            ServiceTestSupport.NewAudit(firstUser.Id));
        var (_, secondRequest) = BuildRequest(secondChallenge.Challenge, firstUser.Handle);
        var limit = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.CompleteAsync(firstUser.Id, secondRequest, ServiceTestSupport.NewAudit(firstUser.Id)));
        Assert.Equal(ApiErrorCodes.DeviceLimitReached, limit.Code);

        var crossChallenge = await service.CreateChallengeAsync(
            secondUser.Id,
            ServiceTestSupport.NewAudit(secondUser.Id));
        var (_, crossRequest) = BuildRequest(crossChallenge.Challenge, secondUser.Handle, firstKey);
        var bound = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.CompleteAsync(secondUser.Id, crossRequest, ServiceTestSupport.NewAudit(secondUser.Id)));
        Assert.Equal(ApiErrorCodes.DeviceAlreadyBound, bound.Code);
        Assert.Equal(0, await db.Devices.CountAsync(candidate => candidate.UserId == secondUser.Id));
    }

    [DatabaseFact]
    public async Task Revoke_recomputes_badge_and_admin_revoke_requires_reason()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var owner = await CreateUserAsync(db, "device-revoke-owner");
        var admin = await CreateUserAsync(db, "device-revoke-admin", UserRoles.Admin);
        var other = await CreateUserAsync(db, "device-revoke-other");
        var service = CreateService(db, new DeviceVerificationOptions());
        var challenge = await service.CreateChallengeAsync(owner.Id, ServiceTestSupport.NewAudit(owner.Id));
        var (_, request) = BuildRequest(challenge.Challenge, owner.Handle);
        var verified = await service.CompleteAsync(owner.Id, request, ServiceTestSupport.NewAudit(owner.Id));
        var deviceId = verified.Device.Id;

        var revoked = await service.RevokeAsync(
            owner.Id,
            deviceId,
            null,
            ServiceTestSupport.NewAudit(owner.Id));
        Assert.Equal(DeviceStatuses.Revoked, revoked.Status);
        Assert.Equal(0, revoked.ActiveDeviceCount);
        Assert.Equal(
            1,
            await db.Notifications.CountAsync(notification =>
                notification.UserId == owner.Id && notification.Type == NotificationTypes.DeviceRevoked));
        Assert.Equal(
            1,
            await db.AuditLogs.CountAsync(log =>
                log.ActorId == owner.Id && log.Action == "device.revoked"));

        var idempotent = await service.RevokeAsync(
            owner.Id,
            deviceId,
            null,
            ServiceTestSupport.NewAudit(owner.Id));
        Assert.Equal(DeviceStatuses.Revoked, idempotent.Status);

        var thirdParty = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.RevokeAsync(other.Id, deviceId, null, ServiceTestSupport.NewAudit(other.Id)));
        Assert.Equal(ApiErrorCodes.NotFound, thirdParty.Code);

        var missingReason = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.RevokeAsync(admin.Id, deviceId, null, ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, missingReason.Code);
    }

    [DatabaseFact]
    public async Task Admin_can_revoke_another_users_device_with_reason()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var owner = await CreateUserAsync(db, "device-admin-owner");
        var admin = await CreateUserAsync(db, "device-admin-actor", UserRoles.Admin);
        var service = CreateService(db, new DeviceVerificationOptions());
        var challenge = await service.CreateChallengeAsync(owner.Id, ServiceTestSupport.NewAudit(owner.Id));
        var (_, request) = BuildRequest(challenge.Challenge, owner.Handle);
        var verified = await service.CompleteAsync(owner.Id, request, ServiceTestSupport.NewAudit(owner.Id));

        var missingReason = await Assert.ThrowsAsync<ApiProblemException>(() =>
            service.RevokeAsync(admin.Id, verified.Device.Id, null, ServiceTestSupport.NewAudit(admin.Id)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, missingReason.Code);

        var revoked = await service.RevokeAsync(
            admin.Id,
            verified.Device.Id,
            "管理员处理",
            ServiceTestSupport.NewAudit(admin.Id));
        Assert.Equal(DeviceStatuses.Revoked, revoked.Status);
        Assert.Equal(0, revoked.ActiveDeviceCount);
        Assert.Equal(
            1,
            await db.AuditLogs.CountAsync(log =>
                log.ActorId == admin.Id && log.Action == "device.revoked_by_admin"));
        Assert.Equal(
            1,
            await db.Notifications.CountAsync(notification =>
                notification.UserId == owner.Id && notification.Type == NotificationTypes.DeviceRevoked));
    }

    [DatabaseFact]
    public async Task Concurrent_completion_of_one_challenge_succeeds_once()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var user = await CreateUserAsync(db, "device-race");
        var challenge = await CreateService(db, new DeviceVerificationOptions())
            .CreateChallengeAsync(user.Id, ServiceTestSupport.NewAudit(user.Id));
        var (_, request) = BuildRequest(challenge.Challenge, user.Handle);

        await using var firstDb = ServiceTestSupport.CreateContext();
        await using var secondDb = ServiceTestSupport.CreateContext();
        var first = CreateService(firstDb, new DeviceVerificationOptions());
        var second = CreateService(secondDb, new DeviceVerificationOptions());
        var results = await Task.WhenAll(
            TryCompleteAsync(first, user.Id, request),
            TryCompleteAsync(second, user.Id, request));

        Assert.Equal(1, results.Count(success => success));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Devices.CountAsync(candidate => candidate.UserId == user.Id));
        Assert.NotNull(
            (await db.DeviceChallenges
                .AsNoTracking()
                .SingleAsync(candidate => candidate.UserId == user.Id)).UsedAt);
    }

    [DatabaseFact]
    public async Task Concurrent_challenges_cannot_exceed_the_active_device_limit()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var user = await CreateUserAsync(db, "device-limit-race");
        var options = new DeviceVerificationOptions { MaxActiveDevices = 1 };
        var service = CreateService(db, options);
        var firstChallenge = await service.CreateChallengeAsync(
            user.Id,
            ServiceTestSupport.NewAudit(user.Id));
        var secondChallenge = await service.CreateChallengeAsync(
            user.Id,
            ServiceTestSupport.NewAudit(user.Id));
        var (_, firstRequest) = BuildRequest(firstChallenge.Challenge, user.Handle);
        var (_, secondRequest) = BuildRequest(secondChallenge.Challenge, user.Handle);

        await using var firstDb = ServiceTestSupport.CreateContext();
        await using var secondDb = ServiceTestSupport.CreateContext();
        var first = CreateService(firstDb, options);
        var second = CreateService(secondDb, options);
        var results = await Task.WhenAll(
            TryCompleteWithLimitAsync(first, user.Id, firstRequest),
            TryCompleteWithLimitAsync(second, user.Id, secondRequest));

        Assert.Equal(1, results.Count(success => success));
        db.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await db.Devices.CountAsync(candidate =>
                candidate.UserId == user.Id && candidate.Status == DeviceStatuses.Active));
    }

    [DatabaseFact]
    public async Task Cleanup_removes_only_challenges_older_than_retention()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var user = await CreateUserAsync(db, "device-cleanup");
        var service = CreateService(db, new DeviceVerificationOptions
        {
            ChallengeRetentionHours = 24
        });
        await service.CreateChallengeAsync(user.Id, ServiceTestSupport.NewAudit(user.Id));
        await service.CreateChallengeAsync(user.Id, ServiceTestSupport.NewAudit(user.Id));
        await service.CreateChallengeAsync(user.Id, ServiceTestSupport.NewAudit(user.Id));
        var challenges = await db.DeviceChallenges
            .Where(candidate => candidate.UserId == user.Id)
            .OrderBy(candidate => candidate.CreatedAt)
            .ToListAsync();
        var now = DateTimeOffset.UtcNow;
        challenges[0].CreatedAt = now.AddHours(-26);
        challenges[0].ExpiresAt = now.AddHours(-25);
        challenges[1].CreatedAt = now.AddHours(-26);
        challenges[1].ExpiresAt = now.AddHours(-25);
        challenges[1].UsedAt = now.AddHours(-25);
        await db.SaveChangesAsync();

        var deleted = await service.CleanupExpiredChallengesAsync();
        Assert.True(deleted >= 2);
        Assert.Equal(
            1,
            await db.DeviceChallenges.CountAsync(candidate => candidate.UserId == user.Id));
    }

    private static Device NewDevice(Guid userId, DateTimeOffset createdAt)
    {
        return new Device
        {
            UserId = userId,
            PublicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            KernelVersion = "6.8.0-45-generic",
            Platform = "linux-x64",
            Label = "测试设备",
            Status = DeviceStatuses.Active,
            CreatedAt = createdAt,
            LastVerifiedAt = createdAt.AddHours(1)
        };
    }

    private static AuditLog NewAudit(
        Guid actorId,
        string action,
        string? targetType,
        Guid? targetId,
        object metadata,
        DateTimeOffset createdAt)
    {
        return new AuditLog
        {
            ActorType = AuditActorTypes.User,
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            MetadataJson = JsonSerializer.SerializeToDocument(metadata),
            CreatedAt = createdAt
        };
    }

    [DatabaseFact]
    public async Task Device_can_be_revoked_again_after_reactivation()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var owner = await CreateUserAsync(db, "device-revoke-again");
        owner.Email = $"device-revoke-again-{ServiceTestSupport.NewSuffix()}@example.test";
        await db.SaveChangesAsync();
        var service = CreateService(db, new DeviceVerificationOptions());
        var challenge = await service.CreateChallengeAsync(owner.Id, ServiceTestSupport.NewAudit(owner.Id));
        var (key, request) = BuildRequest(challenge.Challenge, owner.Handle);
        var verified = await service.CompleteAsync(
            owner.Id,
            request,
            ServiceTestSupport.NewAudit(owner.Id));
        var deviceId = verified.Device.Id;

        var firstRevoke = await service.RevokeAsync(
            owner.Id,
            deviceId,
            null,
            ServiceTestSupport.NewAudit(owner.Id));
        Assert.Equal(DeviceStatuses.Revoked, firstRevoke.Status);

        var reactivateChallenge = await service.CreateChallengeAsync(
            owner.Id,
            ServiceTestSupport.NewAudit(owner.Id));
        var (_, reactivateRequest) = BuildRequest(
            reactivateChallenge.Challenge,
            owner.Handle,
            key);
        var reactivated = await service.CompleteAsync(
            owner.Id,
            reactivateRequest,
            ServiceTestSupport.NewAudit(owner.Id));
        Assert.Equal(deviceId, reactivated.Device.Id);
        Assert.Equal(DeviceStatuses.Active, reactivated.Device.Status);

        var secondRevoke = await service.RevokeAsync(
            owner.Id,
            deviceId,
            null,
            ServiceTestSupport.NewAudit(owner.Id));
        Assert.Equal(DeviceStatuses.Revoked, secondRevoke.Status);
        Assert.Equal(
            2,
            await db.Notifications.CountAsync(notification =>
                notification.UserId == owner.Id && notification.Type == NotificationTypes.DeviceRevoked));
        Assert.Equal(
            2,
            await db.EmailOutbox.CountAsync(outbox =>
                outbox.UserId == owner.Id && outbox.Template == NotificationTypes.DeviceRevoked));

        var idempotent = await service.RevokeAsync(
            owner.Id,
            deviceId,
            null,
            ServiceTestSupport.NewAudit(owner.Id));
        Assert.Equal(DeviceStatuses.Revoked, idempotent.Status);
        Assert.Equal(
            2,
            await db.Notifications.CountAsync(notification =>
                notification.UserId == owner.Id && notification.Type == NotificationTypes.DeviceRevoked));
    }

    [DatabaseFact]
    public async Task Concurrent_revocations_are_serialized_and_idempotent()
    {
        await using var db = ServiceTestSupport.CreateContext();
        var owner = await CreateUserAsync(db, "device-revoke-race");
        var service = CreateService(db, new DeviceVerificationOptions());
        var challenge = await service.CreateChallengeAsync(owner.Id, ServiceTestSupport.NewAudit(owner.Id));
        var (_, request) = BuildRequest(challenge.Challenge, owner.Handle);
        var verified = await service.CompleteAsync(
            owner.Id,
            request,
            ServiceTestSupport.NewAudit(owner.Id));

        await using var firstDb = ServiceTestSupport.CreateContext();
        await using var secondDb = ServiceTestSupport.CreateContext();
        var firstService = CreateService(firstDb, new DeviceVerificationOptions());
        var secondService = CreateService(secondDb, new DeviceVerificationOptions());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<DeviceRevokeView> RevokeAsync(
            DeviceVerificationService revoker,
            WhalePod.Infrastructure.Persistence.WhalePodDbContext context)
        {
            await gate.Task;
            return await revoker.RevokeAsync(
                owner.Id,
                verified.Device.Id,
                null,
                ServiceTestSupport.NewAudit(owner.Id));
        }

        var firstTask = Task.Run(() => RevokeAsync(firstService, firstDb));
        var secondTask = Task.Run(() => RevokeAsync(secondService, secondDb));
        gate.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(DeviceStatuses.Revoked, result.Status));
        db.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await db.Notifications.CountAsync(notification =>
                notification.UserId == owner.Id && notification.Type == NotificationTypes.DeviceRevoked));
        Assert.Equal(
            1,
            await db.AuditLogs.CountAsync(log =>
                log.ActorId == owner.Id && log.Action == "device.revoked"));
    }

    private static async Task<User> CreateUserAsync(
        WhalePod.Infrastructure.Persistence.WhalePodDbContext db,
        string prefix,
        string role = UserRoles.Member)
    {
        var user = ServiceTestSupport.NewUser(prefix, role);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static DeviceVerificationService CreateService(
        WhalePod.Infrastructure.Persistence.WhalePodDbContext db,
        DeviceVerificationOptions options)
    {
        var timeProvider = TimeProvider.System;
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tracker = new DeviceVerificationFailureTracker(
            cache,
            timeProvider,
            Options.Create(options));
        return new DeviceVerificationService(
            db,
            new ActorGuard(db, timeProvider),
            new AdminAccessService(db),
            new EmailNotificationQueue(db),
            tracker,
            Options.Create(options),
            timeProvider,
            NullLogger<DeviceVerificationService>.Instance);
    }

    private static async Task<bool> TryCompleteAsync(
        DeviceVerificationService service,
        Guid userId,
        DeviceCompleteRequest request)
    {
        try
        {
            await service.CompleteAsync(userId, request, ServiceTestSupport.NewAudit(userId));
            return true;
        }
        catch (ApiProblemException exception) when (exception.Code == ApiErrorCodes.ChallengeUsed)
        {
            return false;
        }
    }

    private static async Task<bool> TryCompleteWithLimitAsync(
        DeviceVerificationService service,
        Guid userId,
        DeviceCompleteRequest request)
    {
        try
        {
            await service.CompleteAsync(userId, request, ServiceTestSupport.NewAudit(userId));
            return true;
        }
        catch (ApiProblemException exception)
            when (exception.Code is ApiErrorCodes.DeviceLimitReached or ApiErrorCodes.ChallengeUsed)
        {
            return false;
        }
    }

    private static (DeviceKeyMaterial Key, DeviceCompleteRequest Request) BuildRequest(
        string challenge,
        string handle,
        DeviceKeyMaterial? key = null,
        string kernel = "6.8.0-45-generic",
        string platform = "linux-x64")
    {
        key ??= DeviceCrypto.GenerateKeyMaterial();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var message = DeviceProtocol.BuildMessage(challenge, handle, timestamp, kernel, platform);
        var signature = DeviceCrypto.Sign(key.PrivateKeySeed, message);
        return (
            key,
            new DeviceCompleteRequest(
                1,
                challenge,
                handle,
                timestamp,
                kernel,
                platform,
                key.PublicKeyBase64,
                signature,
                "测试设备"));
    }
}