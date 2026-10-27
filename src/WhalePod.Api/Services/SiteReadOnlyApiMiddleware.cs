using System.Security.Claims;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

public sealed class SiteReadOnlyApiMiddleware(RequestDelegate next, ILogger<SiteReadOnlyApiMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, SiteModeService siteMode)
    {
        if (!IsWriteMethod(context.Request.Method))
        {
            await next(context);
            return;
        }

        var mode = await siteMode.GetAsync(context.RequestAborted);
        if (!mode.ReadOnly
            || (mode.ReadOnlyAllowMemberWrite && IsAuthenticatedMember(context)))
        {
            await next(context);
            return;
        }

        logger.LogInformation(
            "API write blocked by read-only mode path={Path} method={Method} requestId={RequestId}.",
            context.Request.Path.Value,
            context.Request.Method,
            context.TraceIdentifier);
        await ApiEnvelope.WriteProblemAsync(
            context,
            StatusCodes.Status403Forbidden,
            ApiErrorCodes.Forbidden,
            "站点当前为只读模式，写操作已暂停。",
            new { reason = "read_only" });
    }

    private static bool IsAuthenticatedMember(HttpContext context)
    {
        return context.User.Identity?.IsAuthenticated == true
            && !string.Equals(
                context.User.FindFirstValue("auth_type"),
                "service",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWriteMethod(string method)
    {
        return HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);
    }
}