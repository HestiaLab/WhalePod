namespace WhalePod.Infrastructure.Services;

public sealed class DeviceVerificationOptions
{
    public int MaxActiveDevices { get; set; } = 5;
    public int ChallengeTtlSeconds { get; set; } = 300;
    public int TimestampSkewSeconds { get; set; } = 300;
    public int MaxFailedAttempts { get; set; } = 5;
    public int FailureWindowSeconds { get; set; } = 600;
    public int LockoutSeconds { get; set; } = 300;
    public int ChallengeRetentionHours { get; set; } = 24;
    public int ChallengeCleanupIntervalMinutes { get; set; } = 10;
    public int ChallengeRateLimitPerMinute { get; set; } = 10;
    public int CompleteRateLimitPerMinute { get; set; } = 10;
    public int WriteRateLimitPerMinute { get; set; } = 30;
}