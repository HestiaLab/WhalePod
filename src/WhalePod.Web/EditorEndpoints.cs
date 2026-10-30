using System.Security.Claims;

using Microsoft.AspNetCore.Antiforgery;

using WhalePod.Domain;
using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;
using WhalePod.Web.Security;
using WhalePod.Web.Services;

namespace WhalePod.Web;

internal static class EditorEndpoints
{
    private const string EditorPath = "/editor";

    public static void MapEditorEndpoints(this WebApplication app)
    {
        app.MapPost("/editor/images", async (
            HttpContext http,
            IAntiforgery antiforgery,
            ImageUploadService images,
            EditorDraftStore drafts,
            AuditRequestContextFactory auditContextFactory,
            ILogger<Program> logger) =>
        {
            var wantsJson = http.Request.Headers.Accept.ToString()
                .Contains("application/json", StringComparison.OrdinalIgnoreCase);
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!TryActorId(http, out var actorId))
            {
                return Results.Unauthorized();
            }

            try
            {
                if (!http.Request.HasFormContentType)
                {
                    throw ApiProblemException.Validation(
                        "body",
                        "请求必须使用 multipart/form-data，并包含 file 字段。");
                }

                var form = await ReadFormAsync(http);
                if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
                {
                    throw ApiProblemException.Validation("file", "必须且只能上传一个 file 文件字段。");
                }

                var altText = NormalizeAltText(form["alt"].ToString());
                if (file.Length == 0)
                {
                    throw ApiProblemException.Validation("file", "请选择非空图片文件。");
                }

                await using var stream = file.OpenReadStream();
                var result = await images.UploadAsync(
                    actorId,
                    stream,
                    file.Length,
                    auditContextFactory.Create(actorId),
                    http.RequestAborted);
                drafts.AddImage(actorId, new EditorPendingImage(
                    result.Id,
                    result.Url,
                    result.Width,
                    result.Height,
                    result.MimeType,
                    altText ?? string.Empty));

                if (wantsJson)
                {
                    return Results.Json(
                        new
                        {
                            data = new
                            {
                                result.Id,
                                result.Url,
                                result.Width,
                                result.Height,
                                result.MimeType,
                                result.ByteSize,
                                altText
                            }
                        },
                        statusCode: StatusCodes.Status201Created);
                }

                return Results.Redirect($"{EditorPath}?message=image-uploaded");
            }
            catch (ApiProblemException exception)
            {
                if (wantsJson)
                {
                    return Results.Json(
                        new { code = exception.Code, message = exception.Message },
                        statusCode: exception.StatusCode);
                }

                if (exception.Code == ApiErrorCodes.UploadTooLarge)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                return Results.Redirect($"{EditorPath}?error=image-invalid");
            }
        }).RequireAuthorization();

        app.MapPost("/editor/images/{imageId:guid}/remove", async (
            Guid imageId,
            HttpContext http,
            IAntiforgery antiforgery,
            ImageUploadService images,
            EditorDraftStore drafts,
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

            drafts.RemoveImage(actorId, imageId);
            var deleted = await images.DeleteTempAsync(
                actorId,
                imageId,
                auditContextFactory.Create(actorId),
                http.RequestAborted);
            return deleted
                ? Results.Redirect($"{EditorPath}?message=image-removed")
                : Results.Redirect($"{EditorPath}?error=image-missing");
        }).RequireAuthorization();

        app.MapPost("/editor/draft/discard", async (
            HttpContext http,
            IAntiforgery antiforgery,
            EditorDraftStore drafts,
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

            drafts.Clear(actorId);
            return Results.Redirect($"{EditorPath}?message=draft-discarded");
        }).RequireAuthorization();
    }

    private static async Task<IFormCollection> ReadFormAsync(HttpContext http)
    {
        try
        {
            return await http.Request.ReadFormAsync(http.RequestAborted);
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase))
        {
            throw ApiProblemException.UploadTooLarge(MediaLimits.MaxImageBytes);
        }
        catch (InvalidDataException)
        {
            throw ApiProblemException.Validation("body", "multipart 请求格式无效。");
        }
        catch (BadHttpRequestException exception) when (
            exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw ApiProblemException.UploadTooLarge(MediaLimits.MaxImageBytes);
        }
        catch (BadHttpRequestException)
        {
            throw ApiProblemException.Validation("body", "multipart 请求格式无效。");
        }
    }

    private static string? NormalizeAltText(string value)
    {
        var alt = value.Trim();
        if (alt.Length == 0)
        {
            return null;
        }

        if (RuneHelper.Count(alt) > 200
            || alt.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw ApiProblemException.Validation("alt", "图片替代文本不能超过 200 个字符。");
        }

        return alt;
    }

    private static bool TryActorId(HttpContext http, out Guid actorId)
    {
        actorId = Guid.Empty;
        return Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);
    }
}