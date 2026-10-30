using WhalePod.Agent;

namespace WhalePod.Tests.Unit;

public sealed class AgentCliOptionsTests
{
    [Theory]
    [InlineData("https://whalepod.example.com", false, "https")]
    [InlineData("https://localhost:5010", false, "https")]
    [InlineData("http://localhost:5010", true, "http")]
    [InlineData("http://127.0.0.1:5010", true, "http")]
    [InlineData("http://[::1]:5010", true, "http")]
    public void Accepted_api_base_urls(string value, bool allowInsecure, string expectedScheme)
    {
        var uri = AgentCliOptions.ParseApiBaseUrl(value, allowInsecure);
        Assert.Equal(expectedScheme, uri.Scheme);
    }

    [Theory]
    [InlineData("http://whalepod.example.com", false)]
    [InlineData("http://whalepod.example.com", true)]
    [InlineData("http://localhost:5010", false)]
    [InlineData("ftp://localhost", true)]
    [InlineData("https://user:pass@example.com", false)]
    [InlineData("not-a-url", false)]
    public void Rejected_api_base_urls(string value, bool allowInsecure)
    {
        Assert.Throws<InvalidOperationException>(
            () => AgentCliOptions.ParseApiBaseUrl(value, allowInsecure));
    }
}