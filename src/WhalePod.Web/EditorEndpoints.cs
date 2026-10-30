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

        app.MapPost("/editor/create", async (
            HttpContext http,
            IAntiforgery antiforgery,
            PostWriteService posts,
            EditorDraftStore drafts,
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
                return Results.Redirect("/editor?error=auth");
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Guid.TryParse(form["categoryId"], out var categoryId))
            {
                SaveEditorSnapshot(drafts, userId, form);
                return Results.Redirect("/editor?error=validation&field=categoryId&restored=1");
            }

            var imageIdValues = form["imageIds"];
            var imageAltValues = form["imageAlt"];
            List<PostImageInput>? images = null;
            if (imageIdValues.Count != imageAltValues.Count)
            {
                SaveEditorSnapshot(drafts, userId, form);
                return Results.Redirect("/editor?error=validation&field=imageIds&restored=1");
            }

            if (imageIdValues.Count > 10)
            {
                SaveEditorSnapshot(drafts, userId, form);
                return Results.Redirect("/editor?error=validation&field=images&restored=1");
            }

            if (imageIdValues.Count > 0)
            {
                images = new List<PostImageInput>(imageIdValues.Count);
                for (var index = 0; index < imageIdValues.Count; index++)
                {
                    if (!Guid.TryParse(imageIdValues[index], out var imageId))
                    {
                        SaveEditorSnapshot(drafts, userId, form);
                        return Results.Redirect("/editor?error=validation&field=imageIds&restored=1");
                    }

                    var alt = FormValue(form, "imageAlt", index).Trim();
                    images.Add(new PostImageInput(imageId, alt.Length == 0 ? null : alt));
                }

                if (images.Select(image => image.Id).Distinct().Count() != images.Count)
                {
                    SaveEditorSnapshot(drafts, userId, form);
                    return Results.Redirect("/editor?error=validation&field=imageIds&restored=1");
                }
            }

            var artifactCount = Math.Max(
                Math.Max(form["artifactKind"].Count, form["artifactName"].Count),
                Math.Max(form["artifactNote"].Count, form["artifactContent"].Count));
            if (artifactCount > 5)
            {
                SaveEditorSnapshot(drafts, userId, form);
                return Results.Redirect("/editor?error=validation&field=artifacts&restored=1");
            }

            List<ArtifactInput>? artifacts = null;
            for (var index = 0; index < artifactCount; index++)
            {
                var kind = FormValue(form, "artifactKind", index);
                var name = FormValue(form, "artifactName", index).Trim();
                var note = FormValue(form, "artifactNote", index);
                var content = FormValue(form, "artifactContent", index);
                if (kind.Length == 0 && name.Length == 0 && note.Length == 0 && content.Length == 0)
                {
                    continue;
                }

                artifacts ??= [];
                artifacts.Add(new ArtifactInput(
                    kind,
                    name,
                    string.IsNullOrWhiteSpace(note) ? null : note,
                    content));
            }

            var tags = form["tags"]
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var kernel = form["kernel"].ToString().Trim();
            var platform = form["platform"].ToString().Trim();
            var model = form["model"].ToString().Trim();
            var environment = kernel.Length == 0 && platform.Length == 0 && model.Length == 0
                ? null
                : new EnvironmentInput(
                    kernel.Length == 0 ? null : kernel,
                    platform.Length == 0 ? null : platform,
                    model.Length == 0 ? null : model);

            try
            {
                var created = await posts.CreateAsync(
                    userId,
                    new PostCreateCommand(
                        form["title"].ToString(),
                        categoryId,
                        form["bodyMd"].ToString(),
                        tags.Count == 0 ? null : tags,
                        environment,
                        artifacts,
                        images),
                    auditContextFactory.Create(userId),
                    http.RequestAborted);
                drafts.Clear(userId);
                return Results.Redirect($"/posts/{created.Id}?message=post-pending");
            }
            catch (ApiProblemException exception)
            {
                SaveEditorSnapshot(drafts, userId, form);
                return exception.Code switch
                {
                    ApiErrorCodes.AccountMuted => Results.Redirect("/editor?error=muted&restored=1"),
                    ApiErrorCodes.AccountBanned => Results.Redirect("/editor?error=banned&restored=1"),
                    ApiErrorCodes.Forbidden => Results.Redirect("/editor?error=auth&restored=1"),
                    ApiErrorCodes.ValidationFailed => Results.Redirect(
                        $"/editor?error=validation&field={Uri.EscapeDataString(WebEndpointHelpers.DetailField(exception) ?? "unknown")}&restored=1"),
                    _ => Results.Redirect("/editor?error=invalid&restored=1")
                };
            }
        }).RequireAuthorization();

        app.MapPost("/editor/preview", async (
            HttpContext http,
            IAntiforgery antiforgery,
            ILogger<Program> logger) =>
        {
            var failure = await WebFormGuard.ValidateAsync(http, antiforgery, logger);
            if (failure is not null)
            {
                return failure;
            }

            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out _))
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var body = form["bodyMd"].ToString();
            if (RuneHelper.Count(body) > 60_000)
            {
                return Results.BadRequest("正文不能超过 60,000 字。");
            }

            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Content(MarkdownRenderer.Render(body).Html, "text/html; charset=utf-8");
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

    static string FormValue(IFormCollection form, string key, int index)
    {
        var values = form[key];
        return index < values.Count ? values[index] ?? string.Empty : string.Empty;
    }

    static void SaveEditorSnapshot(EditorDraftStore drafts, Guid userId, IFormCollection form)
    {
        var imageIds = form["imageIds"];
        var imageAlts = form["imageAlt"];
        var images = new List<EditorImageRef>();
        var imageCount = Math.Min(imageIds.Count, imageAlts.Count);
        for (var index = 0; index < imageCount; index++)
        {
            if (Guid.TryParse(imageIds[index], out var imageId))
            {
                images.Add(new EditorImageRef(imageId, imageAlts[index] ?? string.Empty));
            }
        }

        var artifacts = new List<EditorArtifact>(5);
        for (var index = 0; index < 5; index++)
        {
            artifacts.Add(new EditorArtifact(
                FormValue(form, "artifactKind", index),
                FormValue(form, "artifactName", index),
                FormValue(form, "artifactNote", index),
                FormValue(form, "artifactContent", index)));
        }

        var tags = form["tags"]
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Take(20)
            .ToList();
        drafts.SaveSnapshot(userId, new EditorSnapshot(
            form["title"].ToString(),
            form["categoryId"].ToString(),
            form["bodyMd"].ToString(),
            form["kernel"].ToString(),
            form["platform"].ToString(),
            form["model"].ToString(),
            tags,
            artifacts,
            images));
    }
}