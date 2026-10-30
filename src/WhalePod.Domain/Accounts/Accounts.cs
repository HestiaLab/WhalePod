using WhalePod.Domain;

namespace WhalePod.Domain.Accounts;

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Handle { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public string? GitHubAvatarUrl { get; set; }
    public long? GitHubId { get; set; }
    public byte[]? GitHubIdHmac { get; set; }
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public short TrustLevel { get; set; }
    public string Status { get; set; } = UserStatuses.Active;
    public string? StatusReason { get; set; }
    public DateTimeOffset? StatusExpiresAt { get; set; }
    public string Role { get; set; } = UserRoles.Member;
    public int SessionVersion { get; set; } = 1;
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ExternalLogin
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "github";
    public string ProviderUserId { get; set; } = string.Empty;
    public string? ProviderLogin { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class InviteCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public byte[] CodeHash { get; set; } = Array.Empty<byte>();
    public string? CodeHint { get; set; }
    public Guid CreatedBy { get; set; }
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string Status { get; set; } = InviteCodeStatuses.Active;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisabledAt { get; set; }
}

public sealed class InviteRedemption
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid InviteCodeId { get; set; }
    public Guid UserId { get; set; }
    public Guid InvitedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Device
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string PublicKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string KernelVersion { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string Status { get; set; } = DeviceStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastVerifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public string? RevokeReason { get; set; }
}

public sealed class DeviceChallenge
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public byte[] ChallengeHash { get; set; } = Array.Empty<byte>();
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public byte[]? RequestIpHash { get; set; }
}