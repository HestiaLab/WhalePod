using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Antiforgery;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;
using WhalePod.Web.Security;

namespace WhalePod.Web;

internal static class InteractionEndpoints
{
    private const string ReportSubmittedMessage = "report-submitted";

    public static void MapInteractionEndpoints(this WebApplication app)
    {
        app.MapPost("/posts/{postId:guid}/like", async (
            Guid postId,
            HttpContext http,
            IAntiforgery antiforgery,
            LikeService likes,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!bool.TryParse(form["liked"], out var liked))
            {
                return Results.BadRequest();
            }

            var returnPath = $"/posts/{postId}";
            try
            {
                await likes.SetPostLikeAsync(
                    actorId,
                    postId,
                    liked,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect(AppendQuery(returnPath, liked ? "message=liked" : "message=unliked"));
            }
            catch (ApiProblemException exception)
            {
                return MapActionError(exception, returnPath);
            }
        }).RequireAuthorization();

        app.MapPost("/comments/{commentId:guid}/like", async (
            Guid commentId,
            HttpContext http,
            IAntiforgery antiforgery,
            LikeService likes,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!bool.TryParse(form["liked"], out var liked)
                || !Guid.TryParse(form["postId"], out var postId))
            {
                return Results.BadRequest();
            }

            var returnPath = $"/posts/{postId}";
            try
            {
                await likes.SetCommentLikeAsync(
                    actorId,
                    commentId,
                    liked,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect(AppendQuery(returnPath, liked ? "message=liked" : "message=unliked"));
            }
            catch (ApiProblemException exception)
            {
                return MapActionError(exception, returnPath);
            }
        }).RequireAuthorization();

        app.MapPost("/comments/{commentId:guid}/accept", async (
            Guid commentId,
            HttpContext http,
            IAntiforgery antiforgery,
            AcceptanceService acceptance,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Guid.TryParse(form["postId"], out var postId))
            {
                return Results.BadRequest();
            }

            var returnPath = $"/posts/{postId}";
            try
            {
                await acceptance.AcceptAsync(
                    actorId,
                    commentId,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect(AppendQuery(returnPath, "message=accepted"));
            }
            catch (ApiProblemException exception)
            {
                return MapAcceptanceError(exception, returnPath);
            }
        }).RequireAuthorization();

        app.MapPost("/comments/{commentId:guid}/unaccept", async (
            Guid commentId,
            HttpContext http,
            IAntiforgery antiforgery,
            AcceptanceService acceptance,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Guid.TryParse(form["postId"], out var postId))
            {
                return Results.BadRequest();
            }

            var returnPath = $"/posts/{postId}";
            try
            {
                await acceptance.UnacceptAsync(
                    actorId,
                    commentId,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect(AppendQuery(returnPath, "message=unaccepted"));
            }
            catch (ApiProblemException exception)
            {
                return MapAcceptanceError(exception, returnPath);
            }
        }).RequireAuthorization();

        app.MapPost("/reports", async (
            HttpContext http,
            IAntiforgery antiforgery,
            ReportService reports,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Guid.TryParse(form["targetId"], out var targetId))
            {
                return Results.BadRequest();
            }

            var returnPath = SafeReturnPath(form["returnUrl"].ToString());
            try
            {
                await reports.CreateAsync(
                    actorId,
                    new ReportCreateCommand(
                        form["targetType"].ToString(),
                        targetId,
                        form["reason"].ToString(),
                        form["detail"].ToString()),
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect(AppendQuery(returnPath, $"message={ReportSubmittedMessage}"));
            }
            catch (ApiProblemException exception)
            {
                if (exception.Code == ApiErrorCodes.NotFound)
                {
                    return Results.NotFound();
                }

                if (exception.Code == ApiErrorCodes.ValidationFailed)
                {
                    return Results.Redirect(AppendQuery(returnPath, "error=report-invalid"));
                }

                return MapActionError(exception, returnPath);
            }
        }).RequireAuthorization();

        app.MapPost("/comments/{commentId:guid}/delete", async (
            Guid commentId,
            HttpContext http,
            IAntiforgery antiforgery,
            CommentWriteService comments,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Guid.TryParse(form["postId"], out var postId)
                || !int.TryParse(form["version"], out var version))
            {
                return Results.BadRequest();
            }

            var returnPath = $"/posts/{postId}";
            if (!string.Equals(form["confirm"], "yes", StringComparison.Ordinal))
            {
                return Results.Redirect(AppendQuery(returnPath, "error=delete-confirm"));
            }

            try
            {
                var result = await comments.DeleteAsync(
                    actorId,
                    commentId,
                    version,
                    form["reason"].ToString(),
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                var message = result.AcceptedCleared ? "message=comment-deleted-solved" : "message=comment-deleted";
                return Results.Redirect(AppendQuery($"/posts/{result.PostId}", message));
            }
            catch (ApiProblemException exception)
            {
                if (exception.Code == ApiErrorCodes.ValidationFailed)
                {
                    return Results.Redirect(AppendQuery(returnPath, "error=delete-invalid"));
                }

                return MapActionError(exception, returnPath);
            }
        }).RequireAuthorization();

        app.MapPost("/posts/{postId:guid}/edit/submit", async (
            Guid postId,
            HttpContext http,
            IAntiforgery antiforgery,
            PostWriteService posts,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!int.TryParse(form["version"], out var version)
                || !Guid.TryParse(form["categoryId"], out var categoryId))
            {
                return Results.Redirect($"/posts/{postId}/edit?error=validation&field=categoryId");
            }

            Guid? changedCategoryId = null;
            if (!Guid.TryParse(form["originalCategoryId"], out var originalCategoryId)
                || originalCategoryId != categoryId)
            {
                changedCategoryId = categoryId;
            }

            var tags = form["tags"]
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var environment = new EnvironmentInput(
                EmptyToNull(form["kernel"].ToString()),
                EmptyToNull(form["platform"].ToString()),
                EmptyToNull(form["model"].ToString()));

            try
            {
                await posts.UpdateAsync(
                    actorId,
                    postId,
                    version,
                    new PostUpdateCommand(
                        form["title"].ToString(),
                        changedCategoryId,
                        form["bodyMd"].ToString(),
                        tags,
                        environment,
                        Artifacts: null,
                        KeepImageIds: null,
                        Images: null,
                        Reason: form["reason"].ToString()),
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                return Results.Redirect($"/posts/{postId}?message=post-updated");
            }
            catch (ApiProblemException exception)
            {
                var editPath = $"/posts/{postId}/edit";
                return exception.Code switch
                {
                    ApiErrorCodes.NotFound => Results.NotFound(),
                    ApiErrorCodes.Conflict => Results.StatusCode(StatusCodes.Status409Conflict),
                    ApiErrorCodes.AccountMuted => Results.Redirect($"{editPath}?error=muted"),
                    ApiErrorCodes.AccountBanned => Results.Redirect($"{editPath}?error=banned"),
                    ApiErrorCodes.Forbidden => ReasonOf(exception) == "activation_required"
                        ? Results.Redirect("/activate")
                        : Results.StatusCode(StatusCodes.Status403Forbidden),
                    ApiErrorCodes.ValidationFailed =>
                        Results.Redirect($"{editPath}?error=validation&field={Uri.EscapeDataString(FieldOf(exception) ?? "unknown")}"),
                    _ => Results.StatusCode(exception.StatusCode)
                };
            }
        }).RequireAuthorization();
    }

    private static IResult MapAcceptanceError(ApiProblemException exception, string returnPath)
    {
        if (exception.Code == ApiErrorCodes.Conflict)
        {
            return Results.Redirect(AppendQuery(returnPath, "error=accept-conflict"));
        }

        return MapActionError(exception, returnPath);
    }

    private static IResult MapActionError(ApiProblemException exception, string returnPath)
    {
        return exception.Code switch
        {
            ApiErrorCodes.NotFound => Results.NotFound(),
            ApiErrorCodes.AccountMuted => Results.Redirect(AppendQuery(returnPath, "error=muted")),
            ApiErrorCodes.AccountBanned => Results.Redirect(AppendQuery(returnPath, "error=banned")),
            ApiErrorCodes.ValidationFailed => Results.Redirect(AppendQuery(returnPath, "error=invalid")),
            ApiErrorCodes.Conflict => Results.Redirect(AppendQuery(returnPath, "error=conflict")),
            ApiErrorCodes.Forbidden => MapForbidden(exception, returnPath),
            _ => Results.StatusCode(exception.StatusCode)
        };
    }

    private static IResult MapForbidden(ApiProblemException exception, string returnPath)
    {
        var reason = ReasonOf(exception);
        if (reason == "activation_required")
        {
            return Results.Redirect("/activate");
        }

        var errorCode = reason switch
        {
            "self_like" => "self-like",
            "self_accept" => "self-accept",
            "not_post_author" => "accept-forbidden",
            _ => "forbidden"
        };
        return Results.Redirect(AppendQuery(returnPath, $"error={errorCode}"));
    }

    private static string SafeReturnPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith('/')
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Any(char.IsControl))
        {
            return "/";
        }

        return value;
    }

    private static string AppendQuery(string path, string query)
    {
        return path.Contains('?', StringComparison.Ordinal) ? $"{path}&{query}" : $"{path}?{query}";
    }

    private static string? EmptyToNull(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool TryActorId(HttpContext http, out Guid actorId)
    {
        actorId = Guid.Empty;
        return Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);
    }

    private static string? ReasonOf(ApiProblemException exception)
    {
        return ReadDetail(exception, "reason");
    }

    private static string? FieldOf(ApiProblemException exception)
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

    private static string? ReadDetail(ApiProblemException exception, string name)
    {
        if (exception.Details is null)
        {
            return null;
        }

        var details = JsonSerializer.SerializeToElement(exception.Details);
        return details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}