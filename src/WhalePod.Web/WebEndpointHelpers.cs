using System.Text.Json;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Web;

internal static class WebEndpointHelpers
{
    public static string? DetailReason(ApiProblemException exception)
    {
        if (exception.Details is null)
        {
            return null;
        }

        var details = JsonSerializer.SerializeToElement(exception.Details);
        return details.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }

    public static string? DetailField(ApiProblemException exception)
    {
        if (exception.Details is null)
        {
            return null;
        }

        var details = JsonSerializer.SerializeToElement(exception.Details);
        return details.TryGetProperty("issues", out var issues)
            && issues.ValueKind == JsonValueKind.Array
            && issues.GetArrayLength() > 0
            && issues[0].TryGetProperty("field", out var field)
            ? field.GetString()
            : null;
    }
}