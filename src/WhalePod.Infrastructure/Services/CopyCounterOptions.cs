namespace WhalePod.Infrastructure.Services;

public sealed class CopyCounterOptions
{
    public string DedupeSecret { get; set; } = string.Empty;
    public int BucketSeconds { get; set; } = 600;
    public int DedupeTtlSeconds { get; set; } = 720;
}