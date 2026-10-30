using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using WhalePod.Contracts.Devices;
using WhalePod.Domain;
using WhalePod.Domain.Accounts;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Persistence;

namespace WhalePod.Infrastructure.Services;

public sealed record DeviceChallengeView(
    string Challenge,
    DateTimeOffset ExpiresAt,
    string ProtocolVersion,
    string CliCommand);

public sealed record DeviceView(
    Guid Id,
    string Fingerprint,
    string KernelVersion,
    string Platform,
    string? Label,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastVerifiedAt,
    DateTimeOffset? RevokedAt);

public sealed record DeviceListView(
    IReadOnlyList<DeviceView> Items,
    int ActiveDeviceCount,
    int MaxActiveDevices);

public sealed record DeviceVerificationResultView(
    DeviceView Device,
    DeviceBadgeView Badge);

public sealed record DeviceRevokeView(
    Guid Id,
    string Status,
    DateTimeOffset RevokedAt,
    int ActiveDeviceCount);

public sealed record AdminDeviceUserView(
    Guid Id,
    string Handle,
    string DisplayName,
    string Status);

public sealed record AdminDeviceItemView(
    Guid Id,
    string Fingerprint,
    string KernelVersion,
    string Platform,
    string? Label,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastVerifiedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedByLabel,
    string? RevokeReason);

public sealed record AdminDeviceEventView(
    Guid Id,
    string Action,
    string ActionLabel,
    DateTimeOffset CreatedAt,
    string ActorLabel,
    string? Detail);

public sealed record AdminDeviceHistoryView(
    AdminDeviceUserView User,
    IReadOnlyList<AdminDeviceItemView> Devices,
    IReadOnlyList<AdminDeviceEventView> Events,
    int ActiveDeviceCount,
    bool DevicesTruncated,
    bool EventsTruncated);

public static class AdminDeviceHistoryLimits
{
    public const int Devices = 100;
    public const int Events = 50;
}

public sealed class DeviceVerificationService(
    WhalePodDbContext db,
    ActorGuard actorGuard,
    AdminAccessService adminAccess,
    EmailNotificationQueue emailQueue,
    DeviceVerificationFailureTracker failures,
    IOptions<DeviceVerificationOptions> options,
    TimeProvider timeProvider,
    ILogger<DeviceVerificationService> logger)
{
    public async Task<DeviceChallengeView> CreateChallengeAsync(
        Guid actorId,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var user = await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var plaintextBytes = RandomNumberGenerator.GetBytes(DeviceProtocol.ChallengeBytes);
        var challenge = DeviceProtocol.Base64UrlEncode(plaintextBytes);
        var expiresAt = now.AddSeconds(Math.Max(1, settings.ChallengeTtlSeconds));
        var entity = new DeviceChallenge
        {
            UserId = actorId,
            ChallengeHash = SHA256.HashData(plaintextBytes),
            ExpiresAt = expiresAt,
            CreatedAt = now,
            RequestIpHash = audit.IpHash
        };

        db.DeviceChallenges.Add(entity);
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "device.challenge_created",
            "device_challenge",
            entity.Id,
            new
            {
                expiresAt,
                protocolVersion = DeviceProtocol.Name
            },
            now));
        await db.SaveChangesAsync(cancellationToken);

        return new DeviceChallengeView(
            challenge,
            expiresAt,
            DeviceProtocol.Name,
            $"whalepod-device sign --challenge {challenge} --handle {user.Handle}");
    }

    public async Task<DeviceVerificationResultView> CompleteAsync(
        Guid actorId,
        DeviceCompleteRequest request,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var user = await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var lockedSeconds = failures.GetLockRemainingSeconds(actorId, audit.IpHash);
        if (lockedSeconds > 0)
        {
            throw ApiProblemException.DeviceLocked(lockedSeconds);
        }

        if (request.Version != DeviceProtocol.Version)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                InvalidField("version", "协议版本必须为 1。"),
                null,
                "version_invalid",
                cancellationToken);
        }

        if (!DeviceProtocol.IsValidChallenge(request.Challenge)
            || !DeviceProtocol.TryDecodeBase64Url(request.Challenge, out var challengeBytes)
            || challengeBytes.Length != DeviceProtocol.ChallengeBytes)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.ChallengeNotFound,
                    400,
                    "挑战不存在，请重新生成。"),
                null,
                "challenge_missing",
                cancellationToken);
        }

        var challengeHash = SHA256.HashData(challengeBytes);
        var challenge = await db.DeviceChallenges
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == actorId
                    && candidate.ChallengeHash == challengeHash,
                cancellationToken);
        if (challenge is null)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.ChallengeNotFound,
                    400,
                    "挑战不存在，请重新生成。"),
                null,
                "challenge_not_found",
                cancellationToken);
        }

        if (challenge.UsedAt is not null)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.ChallengeUsed,
                    400,
                    "挑战已使用，请重新生成。"),
                challenge.Id,
                "challenge_used",
                cancellationToken);
        }

        if (challenge.ExpiresAt <= now)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.ChallengeExpired,
                    400,
                    "挑战已过期，请重新生成。"),
                challenge.Id,
                "challenge_expired",
                cancellationToken);
        }

        var skew = Math.Max(1, settings.TimestampSkewSeconds);
        var nowSeconds = now.ToUnixTimeSeconds();
        if (request.Timestamp < nowSeconds - skew || request.Timestamp > nowSeconds + skew)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.TimestampInvalid,
                    400,
                    "时间戳超出允许偏差，请校准本机时间后重新签名。",
                    new { allowedSkewSeconds = skew }),
                challenge.Id,
                "timestamp_invalid",
                cancellationToken);
        }

        if (!string.Equals(request.Handle, user.Handle, StringComparison.Ordinal))
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                InvalidField("handle", "handle 必须与当前登录用户一致。"),
                challenge.Id,
                "handle_mismatch",
                cancellationToken);
        }

        if (!DeviceProtocol.IsValidKernelVersion(request.KernelVersion))
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                InvalidField("kernelVersion", "内核版本格式无效。"),
                challenge.Id,
                "kernel_invalid",
                cancellationToken);
        }

        if (!DeviceProtocol.IsSupportedPlatform(request.Platform))
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.PlatformUnsupported,
                    400,
                    "平台不在受支持列表内，请使用受支持平台或更新 CLI。",
                    new { field = "platform" }),
                challenge.Id,
                "platform_unsupported",
                cancellationToken);
        }

        if (!DeviceProtocol.TryDecodeBase64(request.PublicKey, out var spki)
            || !DeviceProtocol.TryParseSpki(spki, out var rawPublicKey))
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                InvalidField("publicKey", "公钥格式无效。"),
                challenge.Id,
                "public_key_invalid",
                cancellationToken);
        }

        if (!DeviceProtocol.TryDecodeBase64(request.Signature, out var signature)
            || signature.Length != DeviceProtocol.SignatureBytes)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                InvalidField("signature", "签名格式无效。"),
                challenge.Id,
                "signature_format_invalid",
                cancellationToken);
        }

        var label = NormalizeLabel(request.Label);
        if (label.Invalid)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                InvalidField("label", "设备备注不能超过 40 个字符且不能包含控制字符。"),
                challenge.Id,
                "label_invalid",
                cancellationToken);
        }

        var canonicalPublicKey = Convert.ToBase64String(spki);
        var fingerprint = DeviceProtocol.ComputeFingerprint(spki);
        var message = DeviceProtocol.BuildMessage(
            request.Challenge!,
            user.Handle,
            request.Timestamp,
            request.KernelVersion!,
            request.Platform!);
        if (!DeviceCrypto.VerifyRaw(rawPublicKey, message, signature))
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.SignatureInvalid,
                    400,
                    "签名无效，请检查粘贴内容后重新签名。"),
                challenge.Id,
                "signature_invalid",
                cancellationToken);
        }

        var existing = await db.Devices
            .SingleOrDefaultAsync(
                device => device.PublicKey == canonicalPublicKey
                    || device.Fingerprint == fingerprint,
                cancellationToken);
        var maxActiveDevices = Math.Max(1, settings.MaxActiveDevices);
        if (existing is not null && existing.UserId != actorId)
        {
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.DeviceAlreadyBound,
                    409,
                    "该公钥已绑定其他账号；如属本账号，请撤销旧设备或更换密钥。"),
                challenge.Id,
                "device_already_bound",
                cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM \"user\" WHERE id = {actorId} FOR UPDATE",
            cancellationToken);
        if (existing is not null)
        {
            await db.Entry(existing).ReloadAsync(cancellationToken);
        }

        if (existing is null || existing.Status == DeviceStatuses.Revoked)
        {
            var activeCount = await CountActiveAsync(actorId, cancellationToken);
            if (activeCount >= maxActiveDevices)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw await ThrowFailureAsync(
                    actorId,
                    audit,
                    DeviceLimit(maxActiveDevices),
                    challenge.Id,
                    "device_limit_reached",
                    cancellationToken);
            }
        }

        var reactivated = existing?.Status == DeviceStatuses.Revoked;
        var used = await db.DeviceChallenges
            .Where(candidate => candidate.Id == challenge.Id
                && candidate.UserId == actorId
                && candidate.UsedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.UsedAt, now),
                cancellationToken);
        if (used == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.ChallengeUsed,
                    400,
                    "挑战已使用，请重新生成。"),
                challenge.Id,
                "concurrent_replay",
                cancellationToken);
        }

        Device device;
        if (existing is null)
        {
            device = new Device
            {
                UserId = actorId,
                PublicKey = canonicalPublicKey,
                Fingerprint = fingerprint,
                KernelVersion = request.KernelVersion!,
                Platform = request.Platform!,
                Label = label.Value,
                Status = DeviceStatuses.Active,
                CreatedAt = now,
                LastVerifiedAt = now
            };
            db.Devices.Add(device);
        }
        else
        {
            device = existing;
            device.KernelVersion = request.KernelVersion!;
            device.Platform = request.Platform!;
            device.LastVerifiedAt = now;
            if (label.Value is not null)
            {
                device.Label = label.Value;
            }

            if (reactivated)
            {
                device.Status = DeviceStatuses.Active;
                device.RevokedAt = null;
                device.RevokedBy = null;
                device.RevokeReason = null;
            }
        }

        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            "device.verified",
            "device",
            device.Id,
            new
            {
                fingerprint,
                platform = request.Platform,
                kernelVersion = request.KernelVersion,
                reactivated
            },
            now));
        var payload = new
        {
            summary = "你的设备密钥已验证。",
            targetType = "device",
            targetId = device.Id,
            targetUrl = "/settings/devices"
        };
        var dedupeKey = $"device-verified:{device.Id:N}:{now.UtcTicks}";
        db.Notifications.Add(NotificationWriter.Create(
            actorId,
            NotificationTypes.DeviceVerified,
            payload,
            dedupeKey,
            now));
        await emailQueue.EnqueueForNotificationAsync(
            actorId,
            NotificationTypes.DeviceVerified,
            payload,
            dedupeKey,
            now,
            cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            throw await ThrowFailureAsync(
                actorId,
                audit,
                new ApiProblemException(
                    ApiErrorCodes.DeviceAlreadyBound,
                    409,
                    "该公钥已绑定；如属本账号，请刷新设备列表后重试。"),
                challenge.Id,
                "device_unique_conflict",
                cancellationToken);
        }

        failures.Reset(actorId, audit.IpHash);
        var badge = await BuildBadgeAsync(actorId, cancellationToken);
        return new DeviceVerificationResultView(MapDevice(device), badge);
    }

    public async Task<DeviceListView> ListAsync(
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var devices = await db.Devices
            .AsNoTracking()
            .Where(device => device.UserId == actorId)
            .OrderBy(device => device.Status == DeviceStatuses.Active ? 0 : 1)
            .ThenByDescending(device => device.LastVerifiedAt)
            .ThenByDescending(device => device.Id)
            .ToListAsync(cancellationToken);
        var activeCount = devices.Count(device => device.Status == DeviceStatuses.Active);
        return new DeviceListView(
            devices.Select(MapDevice).ToList(),
            activeCount,
            Math.Max(1, options.Value.MaxActiveDevices));
    }

    public async Task<AdminDeviceHistoryView> GetAdminHistoryAsync(
        Guid actorId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!await adminAccess.IsAdminAsync(actorId, cancellationToken))
        {
            throw ApiProblemException.Forbidden();
        }

        var user = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new AdminDeviceUserView(
                candidate.Id,
                candidate.Handle,
                candidate.DisplayName,
                candidate.Status))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw ApiProblemException.NotFound("用户不存在。");

        var deviceRows = await db.Devices
            .AsNoTracking()
            .Where(device => device.UserId == userId)
            .OrderBy(device => device.Status == DeviceStatuses.Active ? 0 : 1)
            .ThenByDescending(device => device.LastVerifiedAt)
            .ThenByDescending(device => device.Id)
            .Take(AdminDeviceHistoryLimits.Devices + 1)
            .ToListAsync(cancellationToken);
        var devicesTruncated = deviceRows.Count > AdminDeviceHistoryLimits.Devices;
        var devices = devicesTruncated
            ? deviceRows.Take(AdminDeviceHistoryLimits.Devices).ToList()
            : deviceRows;
        var activeDeviceCount = await db.Devices
            .AsNoTracking()
            .CountAsync(
                device => device.UserId == userId && device.Status == DeviceStatuses.Active,
                cancellationToken);

        var deviceIds = db.Devices
            .Where(device => device.UserId == userId)
            .Select(device => device.Id);
        var auditRows = await db.AuditLogs
            .AsNoTracking()
            .Where(log => log.Action.StartsWith("device.")
                && (log.ActorId == userId
                    || (log.TargetId != null && deviceIds.Contains(log.TargetId.Value))))
            .OrderByDescending(log => log.CreatedAt)
            .ThenByDescending(log => log.Id)
            .Take(AdminDeviceHistoryLimits.Events + 1)
            .ToListAsync(cancellationToken);
        var eventsTruncated = auditRows.Count > AdminDeviceHistoryLimits.Events;
        var audits = eventsTruncated
            ? auditRows.Take(AdminDeviceHistoryLimits.Events).ToList()
            : auditRows;

        var actorIds = audits
            .Select(log => log.ActorId)
            .Where(id => id is not null && id != userId)
            .Select(id => id!.Value)
            .Concat(devices
                .Select(device => device.RevokedBy)
                .Where(id => id is not null && id != userId)
                .Select(id => id!.Value))
            .Distinct()
            .ToList();
        var actorHandles = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Users
                .AsNoTracking()
                .Where(candidate => actorIds.Contains(candidate.Id))
                .Select(candidate => new { candidate.Id, candidate.Handle })
                .ToDictionaryAsync(candidate => candidate.Id, candidate => candidate.Handle, cancellationToken);

        var deviceItems = devices
            .Select(device => new AdminDeviceItemView(
                device.Id,
                device.Fingerprint,
                device.KernelVersion,
                device.Platform,
                device.Label,
                device.Status,
                device.CreatedAt,
                device.LastVerifiedAt,
                device.RevokedAt,
                device.RevokedBy is null
                    ? null
                    : MapAdminActorLabel(device.RevokedBy.Value, userId, actorHandles),
                device.RevokeReason))
            .ToList();
        var eventItems = audits
            .Select(log => new AdminDeviceEventView(
                log.Id,
                log.Action,
                MapDeviceActionLabel(log.Action),
                log.CreatedAt,
                MapAdminActorLabel(log.ActorId, userId, actorHandles, log.ActorType),
                ReadDeviceAuditDetail(log.Action, log.MetadataJson)))
            .ToList();

        return new AdminDeviceHistoryView(
            user,
            deviceItems,
            eventItems,
            activeDeviceCount,
            devicesTruncated,
            eventsTruncated);
    }

    public async Task<DeviceRevokeView> RevokeAsync(
        Guid actorId,
        Guid deviceId,
        string? reason,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        await actorGuard.RequireReadAccessAsync(actorId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var device = await db.Devices
            .FromSqlInterpolated($"SELECT * FROM device WHERE id = {deviceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            throw ApiProblemException.NotFound("设备不存在。");
        }

        var isAdmin = await adminAccess.IsAdminAsync(actorId, cancellationToken);
        var ownerRevoke = device.UserId == actorId;
        if (!ownerRevoke && !isAdmin)
        {
            throw ApiProblemException.NotFound("设备不存在。");
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalizedReason is not null
            && (RuneHelper.Count(normalizedReason) > 200
                || normalizedReason.Any(char.IsControl)))
        {
            throw ApiProblemException.Validation("reason", "撤销原因不能超过 200 个字符且不能包含控制字符。");
        }

        if (!ownerRevoke && normalizedReason is null)
        {
            throw ApiProblemException.Validation("reason", "管理员撤销他人设备时必须填写原因。");
        }

        if (device.Status == DeviceStatuses.Revoked)
        {
            return new DeviceRevokeView(
                device.Id,
                device.Status,
                device.RevokedAt ?? now,
                await CountActiveAsync(device.UserId, cancellationToken));
        }

        device.Status = DeviceStatuses.Revoked;
        device.RevokedAt = now;
        device.RevokedBy = actorId;
        device.RevokeReason = normalizedReason;
        db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
            audit,
            ownerRevoke ? "device.revoked" : "device.revoked_by_admin",
            "device",
            device.Id,
            new
            {
                fingerprint = device.Fingerprint,
                reason = normalizedReason,
                ownerUserId = device.UserId
            },
            now));
        var payload = new
        {
            summary = "你的设备已被撤销。",
            targetType = "device",
            targetId = device.Id,
            targetUrl = "/settings/devices"
        };
        var dedupeKey = $"device-revoked:{device.Id:N}:{device.LastVerifiedAt.UtcTicks}";
        var notificationExists = await db.Notifications
            .AsNoTracking()
            .AnyAsync(notification => notification.UserId == device.UserId
                && notification.DedupeKey == dedupeKey, cancellationToken);
        if (!notificationExists)
        {
            db.Notifications.Add(NotificationWriter.Create(
                device.UserId,
                NotificationTypes.DeviceRevoked,
                payload,
                dedupeKey,
                now));
        }

        await emailQueue.EnqueueForNotificationAsync(
            device.UserId,
            NotificationTypes.DeviceRevoked,
            payload,
            dedupeKey,
            now,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DeviceRevokeView(
            device.Id,
            device.Status,
            now,
            await CountActiveAsync(device.UserId, cancellationToken));
    }

    public async Task<int> CleanupExpiredChallengesAsync(
        CancellationToken cancellationToken = default)
    {
        var retentionHours = Math.Max(1, options.Value.ChallengeRetentionHours);
        var cutoff = timeProvider.GetUtcNow().AddHours(-retentionHours);
        return await db.DeviceChallenges
            .Where(challenge => challenge.ExpiresAt < cutoff
                || (challenge.UsedAt != null && challenge.UsedAt < cutoff))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<ApiProblemException> ThrowFailureAsync(
        Guid actorId,
        AuditContext audit,
        ApiProblemException exception,
        Guid? challengeId,
        string reason,
        CancellationToken cancellationToken)
    {
        failures.RecordFailure(actorId, audit.IpHash);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (challengeId is not null)
            {
                await db.DeviceChallenges
                    .Where(candidate => candidate.Id == challengeId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            candidate => candidate.AttemptCount,
                            candidate => candidate.AttemptCount + 1),
                        cancellationToken);
            }

            db.AuditLogs.Add(AuditLogWriter.CreateUserAction(
                audit,
                "device.verification_failed",
                challengeId is null ? null : "device_challenge",
                challengeId,
                new { reason, code = exception.Code },
                now));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception writeException) when (writeException is not OperationCanceledException)
        {
            logger.LogWarning(
                writeException,
                "Failed to record device verification failure for user {UserId}.",
                actorId);
        }

        return exception;
    }

    private Task<int> CountActiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        return db.Devices.CountAsync(
            device => device.UserId == userId && device.Status == DeviceStatuses.Active,
            cancellationToken);
    }

    private async Task<DeviceBadgeView> BuildBadgeAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var activeCount = await CountActiveAsync(userId, cancellationToken);
        var lastVerifiedAt = await db.Devices
            .AsNoTracking()
            .Where(device => device.UserId == userId && device.Status == DeviceStatuses.Active)
            .MaxAsync(device => (DateTimeOffset?)device.LastVerifiedAt, cancellationToken);
        return new DeviceBadgeView(activeCount > 0, activeCount, lastVerifiedAt);
    }

    private static DeviceView MapDevice(Device device)
    {
        return new DeviceView(
            device.Id,
            device.Fingerprint,
            device.KernelVersion,
            device.Platform,
            device.Label,
            device.Status,
            device.CreatedAt,
            device.LastVerifiedAt,
            device.RevokedAt);
    }

    private static string MapAdminActorLabel(
        Guid? actorId,
        Guid userId,
        IReadOnlyDictionary<Guid, string> handles,
        string? actorType = null)
    {
        if (actorType == AuditActorTypes.System)
        {
            return "系统";
        }

        if (actorType == AuditActorTypes.Service)
        {
            return "服务账号";
        }

        if (actorId is null)
        {
            return "系统";
        }

        if (actorId == userId)
        {
            return "本人";
        }

        return handles.TryGetValue(actorId.Value, out var handle)
            ? handle
            : "管理员（已注销）";
    }

    private static string MapDeviceActionLabel(string action)
    {
        return action switch
        {
            "device.challenge_created" => "生成验证挑战",
            "device.verified" => "验证成功",
            "device.verification_failed" => "验证失败",
            "device.revoked" => "成员撤销设备",
            "device.revoked_by_admin" => "管理员撤销设备",
            _ => action
        };
    }

    private static string? ReadDeviceAuditDetail(string action, JsonDocument? metadata)
    {
        if (metadata is null || metadata.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = metadata.RootElement;
        var reason = ReadString(root, "reason");
        var code = ReadString(root, "code");
        var reactivated = root.TryGetProperty("reactivated", out var reactivatedElement)
            && reactivatedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && reactivatedElement.GetBoolean();

        return action switch
        {
            "device.verification_failed" when reason is not null && code is not null =>
                $"原因：{reason}（{code}）",
            "device.verification_failed" when reason is not null => $"原因：{reason}",
            "device.verification_failed" when code is not null => $"错误码：{code}",
            "device.revoked" or "device.revoked_by_admin" when reason is not null => $"原因：{reason}",
            "device.verified" when reactivated => "已重新激活此前撤销的设备",
            _ => null
        };
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length > 200 ? $"{value[..200]}…" : value;
    }

    private static ApiProblemException InvalidField(string field, string message)
    {
        return ApiProblemException.Validation(field, message);
    }

    private static ApiProblemException DeviceLimit(int maxActiveDevices)
    {
        return new ApiProblemException(
            ApiErrorCodes.DeviceLimitReached,
            409,
            "活跃设备数量已达上限，请撤销旧设备后重试。",
            new { maxActiveDevices });
    }

    private static LabelResult NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return new LabelResult(null, false);
        }

        var normalized = label.Trim();
        if (RuneHelper.Count(normalized) > DeviceProtocol.MaxLabelLength
            || normalized.Any(char.IsControl))
        {
            return new LabelResult(null, true);
        }

        return new LabelResult(normalized, false);
    }

    private sealed record LabelResult(string? Value, bool Invalid);
}