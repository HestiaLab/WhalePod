namespace WhalePod.Infrastructure.Services;

public sealed class ViewCounterOptions
{
    public string DedupeSecret { get; set; } = string.Empty;
    public int DedupeWindowSeconds { get; set; } = 600;
}