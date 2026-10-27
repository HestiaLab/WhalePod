using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Antiforgery;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

public sealed class ApiApplicationMarker;

internal static class ApiEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        object? details = null)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message
        };
        if (details is not null)
        {
            payload["details"] = details;
        }

        return context.Response.WriteAsJsonAsync(payload, JsonOptions, context.RequestAborted);
    }
}

internal sealed class ApiAuditContextFactory(IHttpContextAccessor accessor, IConfiguration configuration)
{
    private readonly byte[]? _hashKey = string.IsNullOrWhiteSpace(configuration["Audit:IpHashSecret"])
        ? null
        : System.Text.Encoding.UTF8.GetBytes(configuration["Audit:IpHashSecret"]!);

    public AuditContext Create(Guid actorId)
    {
        var http = accessor.HttpContext;
        var address = http?.Connection.RemoteIpAddress?.ToString();
        byte[]? ipHash = null;
        if (_hashKey is not null && !string.IsNullOrWhiteSpace(address))
        {
            ipHash = System.Security.Cryptography.HMACSHA256.HashData(
                _hashKey,
                System.Text.Encoding.UTF8.GetBytes(address));
        }

        return new AuditContext(actorId, http?.TraceIdentifier, ipHash);
    }
}

internal sealed class CsrfValidationFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            throw new ApiProblemException(
                ApiErrorCodes.Forbidden,
                403,
                "CSRF 校验失败，请刷新令牌后重试。",
                new { reason = "csrf" });
        }

        return await next(context);
    }
}

internal static class EndpointHelpers
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<T> ReadJsonAsync<T>(HttpContext http)
        where T : class
    {
        try
        {
            var body = await http.Request.ReadFromJsonAsync<T>(RequestJsonOptions, http.RequestAborted);
            return body ?? throw ApiProblemException.Validation("body", "请求体不能为空。");
        }
        catch (JsonException)
        {
            throw ApiProblemException.Validation("body", "请求体不是合法 JSON 或包含未知字段。");
        }
        catch (InvalidOperationException)
        {
            throw ApiProblemException.Validation("body", "请求体格式无效。");
        }
        catch (NotSupportedException)
        {
            throw ApiProblemException.Validation("body", "请求体格式无效。");
        }
    }

    public static Guid RequireActorId(HttpContext http)
    {
        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
        {
            throw new ApiProblemException(
                ApiErrorCodes.AuthRequired,
                401,
                "请先登录后再进行此操作。");
        }

        return actorId;
    }

    public static Guid? OptionalActorId(HttpContext http)
    {
        return Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)
            ? actorId
            : null;
    }

    public static int ParseLimit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 20;
        }

        if (!int.TryParse(raw, out var limit) || limit is < 1 or > 50)
        {
            throw ApiProblemException.Validation("limit", "limit 必须是 1 到 50 之间的整数。");
        }

        return limit;
    }

    public static int ParseVersion(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            throw ApiProblemException.Validation("If-Match", "必须携带 If-Match 版本。");
        }

        var trimmed = ifMatch.Trim();
        if (trimmed.Length < 4
            || trimmed[0] != '"'
            || trimmed[^1] != '"'
            || trimmed[1] != 'v')
        {
            throw ApiProblemException.Validation("If-Match", "If-Match 格式必须为 \"vN\"。");
        }

        if (!int.TryParse(trimmed.AsSpan(2, trimmed.Length - 3), out var version)
            || version < 1
            || trimmed[2] == '0')
        {
            throw ApiProblemException.Validation("If-Match", "If-Match 格式必须为 \"vN\"。");
        }

        return version;
    }

    public static int? ParseTrustLevel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, out var level) || level is < 0 or > 4)
        {
            throw ApiProblemException.Validation("trustLevel", "trustLevel 必须在 0–4 之间。");
        }

        return level;
    }

    public static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}