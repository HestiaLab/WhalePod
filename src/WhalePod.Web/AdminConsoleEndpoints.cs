using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Antiforgery;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;
using WhalePod.Web.Security;
using WhalePod.Web.Services;

namespace WhalePod.Web;

internal static class AdminConsoleEndpoints
{
    public static void MapAdminConsoleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization("AdminOnly");

        group.MapPost("/mode/update", async (
            HttpContext http,
            IAntiforgery antiforgery,
            SiteModeService siteMode,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var readOnly = IsChecked(form["readOnly"]);
            var loginDisabled = IsChecked(form["loginDisabled"]);
            var readOnlyAllowMemberWrite = IsChecked(form["readOnlyAllowMemberWrite"]);
            var confirmed = IsChecked(form["confirmMode"]);
            if ((readOnly || loginDisabled) && !confirmed)
            {
                return Results.Redirect("/admin/mode?error=confirm");
            }

            try
            {
                await siteMode.UpdateAsync(
                    actorId,
                    readOnly,
                    loginDisabled,
                    readOnlyAllowMemberWrite,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect("/admin/mode?message=saved");
            }
            catch (ApiProblemException exception)
            {
                logger.LogWarning("Site mode update failed: {Code}.", exception.Code);
                return Results.Redirect($"/admin/mode?error={MapSiteModeError(exception)}");
            }
        });

        group.MapPost("/service-accounts/{accountId:guid}/budget", async (
            Guid accountId,
            HttpContext http,
            IAntiforgery antiforgery,
            ServiceAccountAdminService serviceAccounts,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            int? budget = null;
            var budgetText = form["dailyTokenBudget"].ToString().Trim();
            if (budgetText.Length > 0)
            {
                if (!int.TryParse(budgetText, out var parsed))
                {
                    return Results.Redirect($"/admin/service-accounts/{accountId}?error=budget");
                }

                budget = parsed;
            }

            var outcome = await serviceAccounts.SetDailyTokenBudgetAsync(
                accountId,
                budget,
                auditContextFactory.Create(actorId),
                http.RequestAborted);
            return outcome switch
            {
                ServiceAccountOutcome.Success =>
                    Results.Redirect($"/admin/service-accounts/{accountId}?message=budget"),
                ServiceAccountOutcome.Unchanged =>
                    Results.Redirect($"/admin/service-accounts/{accountId}?message=unchanged"),
                ServiceAccountOutcome.NotFound => Results.NotFound(),
                ServiceAccountOutcome.InvalidInput =>
                    Results.Redirect($"/admin/service-accounts/{accountId}?error=budget"),
                _ => Results.Forbid()
            };
        });

        group.MapPost("/moderation/{targetType}/{targetId:guid}/action", async (
            string targetType,
            Guid targetId,
            HttpContext http,
            IAntiforgery antiforgery,
            ContentModerationService moderation,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!int.TryParse(form["expectedContentVersion"].ToString(), out var expectedVersion)
                || expectedVersion < 1)
            {
                return RedirectModeration(targetType, targetId, "error=version");
            }

            Guid? suggestionId = null;
            if (Guid.TryParse(form["suggestionId"].ToString(), out var parsedSuggestion))
            {
                suggestionId = parsedSuggestion;
            }

            var command = new ModerationActionCommand(
                form["action"].ToString(),
                form["reason"].ToString(),
                expectedVersion,
                suggestionId,
                form["overrideReason"].ToString());
            try
            {
                var result = await moderation.ExecuteAsync(
                    actorId,
                    targetType,
                    targetId,
                    command,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                var message = result.ModerationStatus switch
                {
                    ModerationStatuses.Approved => "approved",
                    ModerationStatuses.Rejected => "rejected",
                    ModerationStatuses.Hidden => "hidden",
                    ModerationStatuses.Deleted => "deleted",
                    _ => "saved"
                };
                return Results.Redirect($"/admin/moderation?message={message}");
            }
            catch (ApiProblemException exception)
            {
                logger.LogWarning(
                    "Moderation action failed for {TargetType}/{TargetId}: {Code}.",
                    targetType,
                    targetId,
                    exception.Code);
                return RedirectModeration(
                    targetType,
                    targetId,
                    $"error={MapModerationError(exception)}");
            }
        });

        group.MapPost("/reports/{reportId:guid}/resolve", async (
            Guid reportId,
            HttpContext http,
            IAntiforgery antiforgery,
            ReportService reports,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var actionType = form["actionType"].ToString().Trim();
            var actions = actionType.Length == 0
                ? null
                : new List<ReportResolveActionInput>
                {
                    new(
                        actionType,
                        form["actionReason"].ToString(),
                        ParseUtcDateTime(form["actionExpiresAt"].ToString()))
                };
            try
            {
                await reports.ResolveAsync(
                    actorId,
                    reportId,
                    new ReportResolveCommand(
                        form["outcome"].ToString(),
                        form["resolutionNote"].ToString(),
                        actions),
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect("/admin/reports?message=handled");
            }
            catch (ApiProblemException exception)
            {
                logger.LogWarning(
                    "Report resolution failed for {ReportId}: {Code}.",
                    reportId,
                    exception.Code);
                return Results.Redirect($"/admin/reports?error={MapReportError(exception)}");
            }
        });

        group.MapPost("/users/{userId:guid}/status", async (
            Guid userId,
            HttpContext http,
            IAntiforgery antiforgery,
            AdminUserService users,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            try
            {
                await users.ChangeStatusAsync(
                    actorId,
                    userId,
                    form["status"].ToString(),
                    form["reason"].ToString(),
                    ParseUtcDateTime(form["expiresAt"].ToString()),
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect($"/admin/users/{userId}?message=status");
            }
            catch (ApiProblemException exception)
            {
                logger.LogWarning(
                    "User status change failed for {UserId}: {Code}.",
                    userId,
                    exception.Code);
                return Results.Redirect(
                    $"/admin/users/{userId}?error={MapAdminUserError(exception)}");
            }
        });

        group.MapPost("/users/{userId:guid}/trust", async (
            Guid userId,
            HttpContext http,
            IAntiforgery antiforgery,
            AdminUserService users,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!short.TryParse(form["trustLevel"].ToString(), out var trustLevel))
            {
                return Results.Redirect($"/admin/users/{userId}?error=trust");
            }

            try
            {
                await users.ChangeTrustLevelAsync(
                    actorId,
                    userId,
                    trustLevel,
                    form["reason"].ToString(),
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect($"/admin/users/{userId}?message=trust");
            }
            catch (ApiProblemException exception)
            {
                logger.LogWarning(
                    "User trust change failed for {UserId}: {Code}.",
                    userId,
                    exception.Code);
                return Results.Redirect(
                    $"/admin/users/{userId}?error={MapAdminUserError(exception)}");
            }
        });

        group.MapPost("/categories/create", async (
            HttpContext http,
            IAntiforgery antiforgery,
            CategoryAdminService categories,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!int.TryParse(form["displayOrder"].ToString(), out var displayOrder))
            {
                return Results.Redirect("/admin/categories?error=invalid");
            }

            var result = await categories.CreateAsync(
                actorId,
                form["slug"].ToString(),
                form["name"].ToString(),
                form["description"].ToString(),
                displayOrder,
                auditContextFactory.Create(actorId),
                http.RequestAborted);
            return result.Outcome == CategoryMutationOutcome.Success
                ? Results.Redirect("/admin/categories?message=created")
                : Results.Redirect($"/admin/categories?error={MapCategoryError(result)}");
        });

        group.MapPost("/categories/{categoryId:guid}/update", async (
            Guid categoryId,
            HttpContext http,
            IAntiforgery antiforgery,
            CategoryAdminService categories,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!int.TryParse(form["displayOrder"].ToString(), out var displayOrder))
            {
                return Results.Redirect("/admin/categories?error=invalid");
            }

            var result = await categories.UpdateAsync(
                actorId,
                categoryId,
                form["name"].ToString(),
                form["description"].ToString(),
                displayOrder,
                auditContextFactory.Create(actorId),
                http.RequestAborted);
            return result.Outcome switch
            {
                CategoryMutationOutcome.Success =>
                    Results.Redirect("/admin/categories?message=updated"),
                CategoryMutationOutcome.Unchanged =>
                    Results.Redirect("/admin/categories?message=unchanged"),
                _ => Results.Redirect($"/admin/categories?error={MapCategoryError(result)}")
            };
        });

        group.MapPost("/categories/{categoryId:guid}/visibility", async (
            Guid categoryId,
            HttpContext http,
            IAntiforgery antiforgery,
            CategoryAdminService categories,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var disabled = bool.TryParse(form["disabled"].ToString(), out var parsedDisabled)
                && parsedDisabled;
            var confirmImpact = bool.TryParse(form["confirmImpact"].ToString(), out var confirmed)
                && confirmed;
            var result = await categories.SetStatusAsync(
                actorId,
                categoryId,
                disabled,
                confirmImpact,
                auditContextFactory.Create(actorId),
                http.RequestAborted);
            return result.Outcome switch
            {
                CategoryMutationOutcome.Success =>
                    Results.Redirect(disabled
                        ? "/admin/categories?message=disabled"
                        : "/admin/categories?message=enabled"),
                CategoryMutationOutcome.Unchanged =>
                    Results.Redirect("/admin/categories?message=unchanged"),
                _ => Results.Redirect($"/admin/categories?error={MapCategoryError(result)}")
            };
        });
        var profile = app.MapGroup("/settings/profile")
            .RequireAuthorization();
        profile.MapPost("/avatar", async (
            HttpContext http,
            IAntiforgery antiforgery,
            ImageUploadService uploads,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var file = form.Files.GetFile("avatar");
            if (file is null || file.Length == 0)
            {
                return Results.Redirect("/settings/profile?error=avatar-file");
            }

            try
            {
                await using var stream = file.OpenReadStream();
                await uploads.UploadAvatarAsync(
                    actorId,
                    stream,
                    file.Length,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect("/settings/profile?message=avatar-updated");
            }
            catch (ApiProblemException exception)
            {
                logger.LogWarning(
                    "Avatar upload failed for user {UserId}: {Code}.",
                    actorId,
                    exception.Code);
                var error = exception.Code switch
                {
                    ApiErrorCodes.UploadUnsupportedType => "avatar-type",
                    ApiErrorCodes.ValidationFailed or ApiErrorCodes.UploadTooLarge => "avatar-size",
                    ApiErrorCodes.AccountBanned or ApiErrorCodes.Forbidden => "avatar-auth",
                    _ => "avatar-invalid"
                };
                return Results.Redirect($"/settings/profile?error={error}");
            }
        });

        profile.MapPost("/avatar/clear", async (
            HttpContext http,
            IAntiforgery antiforgery,
            ImageUploadService uploads,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await ValidateFormAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Forbid();
            }

            try
            {
                await uploads.ClearAvatarAsync(
                    actorId,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect("/settings/profile?message=avatar-cleared");
            }
            catch (ApiProblemException exception)
            {
                logger.LogWarning(
                    "Avatar clear failed for user {UserId}: {Code}.",
                    actorId,
                    exception.Code);
                return Results.Redirect("/settings/profile?error=avatar-invalid");
            }
        });
    }

    private static IResult RedirectModeration(string targetType, Guid targetId, string query)
    {
        return Results.Redirect($"/admin/moderation/{targetType}/{targetId}?{query}");
    }

    private static bool TryActorId(HttpContext http, out Guid actorId)
    {
        return Guid.TryParse(
            http.User.FindFirstValue(ClaimTypes.NameIdentifier),
            out actorId);
    }

    private static bool IsChecked(string? value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static DateTimeOffset? ParseUtcDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string MapModerationError(ApiProblemException exception)
    {
        var reason = DetailReason(exception);
        return exception.Code switch
        {
            ApiErrorCodes.ValidationFailed => reason == "overrideReason" ? "override" : "validation",
            ApiErrorCodes.Conflict or ApiErrorCodes.ContentVersionStale => "stale",
            ApiErrorCodes.NotFound => "missing",
            ApiErrorCodes.Forbidden => "forbidden",
            _ => "invalid"
        };
    }

    private static string MapReportError(ApiProblemException exception)
    {
        return exception.Code switch
        {
            ApiErrorCodes.ValidationFailed => "validation",
            ApiErrorCodes.Conflict => "conflict",
            ApiErrorCodes.NotFound => "missing",
            ApiErrorCodes.Forbidden => "forbidden",
            _ => "invalid"
        };
    }

    private static string MapAdminUserError(ApiProblemException exception)
    {
        var reason = DetailReason(exception);
        if (reason is "self_operation" or "self_demote")
        {
            return "self";
        }

        if (reason == "last_admin")
        {
            return "last-admin";
        }

        return exception.Code switch
        {
            ApiErrorCodes.ValidationFailed => "validation",
            ApiErrorCodes.Conflict => "conflict",
            ApiErrorCodes.NotFound => "missing",
            ApiErrorCodes.Forbidden => "forbidden",
            _ => "invalid"
        };
    }

    private static string MapCategoryError(CategoryMutationResult result)
    {
        return result.Outcome switch
        {
            CategoryMutationOutcome.DuplicateSlug => "duplicate",
            CategoryMutationOutcome.NotFound => "missing",
            CategoryMutationOutcome.ConfirmationRequired => "impact",
            _ => "invalid"
        };
    }

    private static string MapSiteModeError(ApiProblemException exception)
    {
        return exception.Code switch
        {
            ApiErrorCodes.Forbidden => "forbidden",
            ApiErrorCodes.ValidationFailed => "validation",
            _ => "invalid"
        };
    }

    private static string? DetailReason(ApiProblemException exception)
    {
        if (exception.Details is null)
        {
            return null;
        }

        try
        {
            var element = JsonSerializer.SerializeToElement(exception.Details);
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IResult?> ValidateFormAsync(
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

    private static bool IsSameOrigin(HttpContext context)
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