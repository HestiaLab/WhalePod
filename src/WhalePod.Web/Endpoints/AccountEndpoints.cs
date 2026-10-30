using System.Globalization;
using System.Net;
using System.Security.Claims;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

using WhalePod.Contracts.Devices;
using WhalePod.Domain;
using WhalePod.Infrastructure.Email;
using WhalePod.Infrastructure.Services;
using WhalePod.Web.Security;
using WhalePod.Web.Services;

namespace WhalePod.Web.Endpoints;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/settings/profile/update", async (
            HttpContext http,
            IAntiforgery antiforgery,
            ProfileSettingsService profileSettings,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var outcome = await profileSettings.UpdateProfileAsync(
                userId,
                form["displayName"].ToString(),
                form["bio"].ToString(),
                auditContextFactory.Create(userId),
                http.RequestAborted);

            return outcome switch
            {
                SettingsUpdateResult.Success => Results.Redirect("/settings/profile?message=updated"),
                SettingsUpdateResult.Unchanged => Results.Redirect("/settings/profile?message=unchanged"),
                SettingsUpdateResult.InvalidInput => Results.Redirect("/settings/profile?error=invalid"),
                SettingsUpdateResult.NotFound => Results.NotFound(),
                _ => Results.Forbid()
            };
        }).RequireAuthorization();

        app.MapPost("/settings/emails/update", async (
            HttpContext http,
            IAntiforgery antiforgery,
            ProfileSettingsService profileSettings,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var repliesMentions = form["repliesMentions"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            var moderationResults = form["moderationResults"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

            var outcome = await profileSettings.UpdateEmailPreferencesAsync(
                userId,
                repliesMentions,
                moderationResults,
                auditContextFactory.Create(userId),
                http.RequestAborted);

            return outcome switch
            {
                SettingsUpdateResult.Success => Results.Redirect("/settings/emails?message=updated"),
                SettingsUpdateResult.Unchanged => Results.Redirect("/settings/emails?message=unchanged"),
                SettingsUpdateResult.NotFound => Results.NotFound(),
                _ => Results.Forbid()
            };
        }).RequireAuthorization();

        app.MapPost("/settings/account/delete", async (
            HttpContext http,
            IAntiforgery antiforgery,
            AccountDeletionService deletion,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!form["confirmPermanent"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Redirect("/settings/account?error=confirm");
            }

            try
            {
                await deletion.DeleteAsync(
                    userId,
                    userId,
                    form["deleteContent"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase),
                    form["confirmHandle"].ToString(),
                    auditContextFactory.Create(userId),
                    http.RequestAborted);
            }
            catch (ApiProblemException exception)
            {
                return Results.Redirect($"/settings/account?error={MapAccountDeletionError(exception)}");
            }

            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/account/deleted");
        }).RequireAuthorization();

        app.MapPost("/settings/devices/challenge", async (
            HttpContext http,
            IAntiforgery antiforgery,
            DeviceVerificationService deviceVerification,
            DeviceChallengeRevealCache revealCache,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Redirect("/activate");
            }

            try
            {
                var challenge = await deviceVerification.CreateChallengeAsync(
                    userId,
                    auditContextFactory.Create(userId),
                    http.RequestAborted);
                revealCache.Store(userId, challenge);
                return Results.Redirect("/settings/devices?challenge=created");
            }
            catch (ApiProblemException exception)
            {
                return Results.Redirect($"/settings/devices?error={MapDeviceError(exception)}");
            }
        }).RequireAuthorization().RequireRateLimiting("device-challenge");

        app.MapPost("/settings/devices/complete", async (
            HttpContext http,
            IAntiforgery antiforgery,
            DeviceVerificationService deviceVerification,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Redirect("/activate");
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var payload = form["payload"].ToString();
            var label = form["label"].ToString();
            DeviceCompleteRequest request;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                if (payload.Length > 20_000 || !DevicePayloadParser.TryParse(payload, out var parsed) || parsed is null)
                {
                    return Results.Redirect("/settings/devices?error=format");
                }

                request = parsed;
            }
            else
            {
                if (!long.TryParse(
                        form["manualTimestamp"].ToString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var timestamp))
                {
                    return Results.Redirect("/settings/devices?error=format");
                }

                request = new DeviceCompleteRequest(
                    1,
                    form["manualChallenge"].ToString(),
                    string.IsNullOrWhiteSpace(form["manualHandle"])
                        ? http.User.Identity?.Name
                        : form["manualHandle"].ToString(),
                    timestamp,
                    form["manualKernelVersion"].ToString(),
                    form["manualPlatform"].ToString(),
                    form["manualPublicKey"].ToString(),
                    form["manualSignature"].ToString(),
                    null);
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                request = request with { Label = label };
            }

            try
            {
                await deviceVerification.CompleteAsync(
                    userId,
                    request,
                    auditContextFactory.Create(userId),
                    http.RequestAborted);
                return Results.Redirect("/settings/devices?message=verified");
            }
            catch (ApiProblemException exception)
            {
                return Results.Redirect($"/settings/devices?error={MapDeviceError(exception)}");
            }
        }).RequireAuthorization().RequireRateLimiting("device-complete");

        app.MapPost("/settings/devices/{deviceId:guid}/revoke", async (
            Guid deviceId,
            HttpContext http,
            IAntiforgery antiforgery,
            DeviceVerificationService deviceVerification,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Redirect("/activate");
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            try
            {
                await deviceVerification.RevokeAsync(
                    userId,
                    deviceId,
                    form["reason"].ToString(),
                    auditContextFactory.Create(userId),
                    http.RequestAborted);
                return Results.Redirect("/settings/devices?message=revoked");
            }
            catch (ApiProblemException exception)
            {
                return Results.Redirect($"/settings/devices?error={MapDeviceError(exception)}");
            }
        }).RequireAuthorization().RequireRateLimiting("device-write");

        app.MapPost("/appeals/submit", async (
            HttpContext http,
            IAntiforgery antiforgery,
            AppealService appeals,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var targetType = form["targetType"].ToString();
            var targetIdText = form["targetId"].ToString().Trim();
            var targetId = userId;
            if (!targetType.Equals("account_penalty", StringComparison.OrdinalIgnoreCase)
                && !Guid.TryParse(targetIdText, out targetId))
            {
                return Results.Redirect("/appeals?error=target");
            }

            try
            {
                await appeals.SubmitAsync(
                    userId,
                    targetType,
                    targetId,
                    form["reason"].ToString(),
                    auditContextFactory.Create(userId),
                    http.RequestAborted);
                return Results.Redirect("/appeals?message=submitted");
            }
            catch (ApiProblemException exception)
            {
                return Results.Redirect($"/appeals?error={MapAppealSubmitError(exception)}");
            }
        }).RequireAuthorization();

        app.MapPost("/admin/appeals/{appealId}/review", async (
            string appealId,
            HttpContext http,
            IAntiforgery antiforgery,
            AppealService appeals,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
                || !Guid.TryParse(appealId, out var parsedAppealId))
            {
                return Results.NotFound();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            try
            {
                await appeals.ReviewAsync(
                    adminId,
                    parsedAppealId,
                    new AppealReviewCommand(form["outcome"].ToString(), form["note"].ToString()),
                    auditContextFactory.Create(adminId),
                    http.RequestAborted);
                return Results.Redirect("/admin/appeals?message=reviewed");
            }
            catch (ApiProblemException exception)
            {
                return Results.Redirect($"/admin/appeals?error={MapAppealReviewError(exception)}");
            }
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/email/unsubscribe", async (
            string? token,
            HttpContext http,
            EmailUnsubscribeService unsubscribe,
            EmailUnsubscribeTokenService tokens,
            AuditRequestContextFactory auditContextFactory) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!tokens.TryValidate(token, out var userId, out _, out _))
            {
                return UnsubscribeResult(StatusCodes.Status400BadRequest, "链接无效", "退订链接无效或已过期。");
            }

            var outcome = await unsubscribe.UnsubscribeAsync(token, auditContextFactory.Create(userId));
            return outcome switch
            {
                EmailUnsubscribeOutcome.Unsubscribed => UnsubscribeResult(
                    StatusCodes.Status200OK,
                    "退订成功",
                    "此类邮件已退订；账号与安全邮件仍会发送。"),
                EmailUnsubscribeOutcome.AlreadyDisabled => UnsubscribeResult(
                    StatusCodes.Status200OK,
                    "已退订",
                    "此类邮件此前已退订。"),
                EmailUnsubscribeOutcome.UserMissing => UnsubscribeResult(
                    StatusCodes.Status404NotFound,
                    "账号不可用",
                    "账号不存在或已注销。"),
                _ => UnsubscribeResult(
                    StatusCodes.Status400BadRequest,
                    "链接无效",
                    "退订链接无效或已过期。")
            };
        });
    }

    static string MapDeviceError(ApiProblemException exception)
    {
        return exception.Code switch
        {
            ApiErrorCodes.ChallengeNotFound => "challenge-not-found",
            ApiErrorCodes.ChallengeExpired => "challenge-expired",
            ApiErrorCodes.ChallengeUsed => "challenge-used",
            ApiErrorCodes.SignatureInvalid => "signature-invalid",
            ApiErrorCodes.TimestampInvalid => "timestamp-invalid",
            ApiErrorCodes.PlatformUnsupported => "platform-unsupported",
            ApiErrorCodes.DeviceAlreadyBound => "device-bound",
            ApiErrorCodes.DeviceLimitReached => "device-limit",
            ApiErrorCodes.DeviceVerificationLocked => "locked",
            ApiErrorCodes.Forbidden => "forbidden",
            ApiErrorCodes.AuthRequired => "auth",
            ApiErrorCodes.NotFound => "not-found",
            _ => "invalid"
        };
    }

    static string MapAccountDeletionError(ApiProblemException exception)
    {
        if (exception.Code == ApiErrorCodes.Forbidden)
        {
            return WebEndpointHelpers.DetailReason(exception) == "last_admin" ? "last-admin" : "invalid";
        }

        return exception.Code switch
        {
            ApiErrorCodes.Conflict => "conflict",
            ApiErrorCodes.ValidationFailed => "confirm",
            _ => "invalid"
        };
    }

    static string MapAppealSubmitError(ApiProblemException exception)
    {
        return exception.Code switch
        {
            ApiErrorCodes.Forbidden => "forbidden",
            ApiErrorCodes.NotFound => "target",
            ApiErrorCodes.Conflict => "conflict",
            ApiErrorCodes.ValidationFailed => WebEndpointHelpers.DetailField(exception) == "reason" ? "reason" : "target",
            _ => "invalid"
        };
    }

    static string MapAppealReviewError(ApiProblemException exception)
    {
        return exception.Code switch
        {
            ApiErrorCodes.Forbidden => "self",
            ApiErrorCodes.NotFound => "missing",
            ApiErrorCodes.Conflict => "conflict",
            ApiErrorCodes.ValidationFailed => "outcome",
            _ => "invalid"
        };
    }

    static IResult UnsubscribeResult(int statusCode, string title, string message)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedMessage = WebUtility.HtmlEncode(message);
        return Results.Text(
            $"""
            <!doctype html>
            <html lang="zh-CN">
            <head><meta charset="utf-8" /><meta name="robots" content="noindex" /><title>{encodedTitle} - WhalePod</title></head>
            <body>
            <main>
            <h1>{encodedTitle}</h1>
            <p>{encodedMessage}</p>
            <p><a href="/settings/emails">管理邮件偏好</a> · <a href="/">返回首页</a></p>
            </main>
            </body>
            </html>
            """,
            "text/html; charset=utf-8",
            statusCode: statusCode);
    }
}