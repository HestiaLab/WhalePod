using System.Security.Claims;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Web.Security;

public sealed class SiteReadOnlyMiddleware(RequestDelegate next, ILogger<SiteReadOnlyMiddleware> logger)
{
    private static readonly HashSet<string> ExemptPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/logout",
        "/admin/mode",
        "/internal/counters/copy"
    };

    public async Task InvokeAsync(HttpContext context, SiteModeService siteMode)
    {
        if (!IsWriteMethod(context.Request.Method)
            || ExemptPaths.Any(path => context.Request.Path.StartsWithSegments(path)))
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
            "Write blocked by read-only mode path={Path} method={Method} requestId={RequestId}.",
            context.Request.Path.Value,
            context.Request.Method,
            context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            """
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head><meta charset="utf-8" /><meta name="robots" content="noindex" /><title>站点只读 - WhalePod</title></head>
            <body>
              <main>
                <h1>站点当前为只读模式</h1>
                <p>浏览与搜索仍可用，但发帖、评论、点赞、举报、设备与账号设置等写操作已暂停。</p>
                <p><a href="/">返回首页</a></p>
              </main>
            </body>
            </html>
            """);
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