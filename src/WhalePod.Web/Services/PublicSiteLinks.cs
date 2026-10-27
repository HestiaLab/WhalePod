namespace WhalePod.Web.Services;

public sealed class PublicSiteLinks(IConfiguration configuration)
{
    public string BaseUrl { get; } = Normalize(
        configuration["PublicSite:BaseUrl"] ?? "http://localhost:5224");

    public string Absolute(string path)
    {
        return $"{BaseUrl}/{path.TrimStart('/')}";
    }

    public string? AbsoluteMedia(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.StartsWith('/') ? Absolute(value) : value;
    }

    private static string Normalize(string value)
    {
        return value.Trim().TrimEnd('/');
    }
}