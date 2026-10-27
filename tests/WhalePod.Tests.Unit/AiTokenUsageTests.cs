using System.Text.Json;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Tests.Unit;

public sealed class AiTokenUsageTests
{
    [Fact]
    public void Total_takes_precedence_and_fallback_sums_numeric_fields()
    {
        using var withTotal = JsonDocument.Parse(
            """{"prompt":100,"completion":20,"total":120}""");
        using var withoutTotal = JsonDocument.Parse(
            """{"prompt":100,"completion":20,"cached":5}""");
        using var negative = JsonDocument.Parse(
            """{"prompt":100,"completion":-5}""");
        using var array = JsonDocument.Parse("[1,2,3]");

        Assert.Equal(120, AiTokenUsage.Sum(withTotal));
        Assert.Equal(125, AiTokenUsage.Sum(withoutTotal));
        Assert.Equal(100, AiTokenUsage.Sum(negative));
        Assert.Equal(0, AiTokenUsage.Sum(array));
        Assert.Equal(0, AiTokenUsage.Sum(null));
    }
}