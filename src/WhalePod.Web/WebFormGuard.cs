using Microsoft.AspNetCore.Antiforgery;

namespace WhalePod.Web;

internal static class WebFormGuard
{
    public static async Task<IResult?> ValidateAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        ILogger logger)
    {
        if (!IsSameOrigin(http))
        {
            logger.LogWarning("Form rejected for cross-origin request to {Path}.", http.Request.Path);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            logger.LogWarning(
                "Form rejected for a missing or invalid antiforgery token at {Path}.",
                http.Request.Path);
            return Results.BadRequest();
        }
    }

    public static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var requestPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
            return string.Equals(uri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
                && uri.Port == requestPort;
        }

        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        return string.IsNullOrWhiteSpace(fetchSite) || fetchSite is "same-origin" or "none";
    }
}