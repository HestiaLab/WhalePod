namespace WhalePod.Infrastructure.Services;

public sealed class RetentionOptions
{
    public int NotificationRetentionDays { get; set; } = 180;
    public int EmailOutboxRetentionDays { get; set; } = 30;
    public int CleanupIntervalMinutes { get; set; } = 60;
    public int BatchSize { get; set; } = 500;
}

public sealed class HealthOptions
{
    public int EmailBacklogWarningThreshold { get; set; } = 50;
    public int MediaCleanupBacklogWarningThreshold { get; set; } = 100;
    public int DeviceChallengeBacklogWarningThreshold { get; set; } = 100;
    public int CopyDedupeBacklogWarningThreshold { get; set; } = 1000;
    public int ModerationTaskBacklogWarningThreshold { get; set; } = 100;
}