using System.Net;
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

    public static string StatusPageHtml(int statusCode, string title, string message)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedMessage = WebUtility.HtmlEncode(message);
        return $"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <meta name="robots" content="noindex" />
              <meta name="color-scheme" content="light dark" />
              <title>{encodedTitle} - WhalePod</title>
              <link rel="stylesheet" href="/app.css" />
            </head>
            <body>
              <a class="skip-link" href="#main-content">跳到正文</a>
              <header class="site-header">
                <div class="site-header-inner">
                  <a class="brand" href="/">
                    <img class="brand-favicon" src="/favicon.svg" alt="" width="26" height="26" />
                    <span class="brand-lockup"><span class="brand-word">WhalePod</span><span class="brand-cn">鲸群</span></span>
                  </a>
                </div>
              </header>
              <main id="main-content" class="site-main" tabindex="-1">
                <div class="status-page">
                  <p class="status-code">HTTP {statusCode}</p>
                  <h1>{encodedTitle}</h1>
                  <p>{encodedMessage}</p>
                  <div class="status-actions">
                    <a class="button" href="/">返回信息流</a>
                    <a class="button button-secondary" href="/search">搜索内容</a>
                  </div>
                </div>
              </main>
            </body>
            </html>
            """;
    }
}