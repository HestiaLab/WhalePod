using Microsoft.AspNetCore.RateLimiting;

using WhalePod.Infrastructure.Services;
using WhalePod.Infrastructure.Storage;

namespace WhalePod.Api.Endpoints;

internal static class UploadEndpoints
{
    public static void MapUploadEndpoints(this WebApplication app)
    {
        var uploads = app.MapGroup("/api/v1")
            .RequireAuthorization()
            .AddEndpointFilter<CsrfValidationFilter>()
            .RequireRateLimiting("api-upload");

        uploads.MapPost("/uploads/images", async (
            HttpContext http,
            ImageUploadService images,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!http.Request.HasFormContentType)
            {
                throw ApiProblemException.Validation(
                    "body",
                    "请求必须使用 multipart/form-data，并包含 file 字段。");
            }

            IFormCollection form;
            try
            {
                form = await http.Request.ReadFormAsync(http.RequestAborted);
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

            if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
            {
                throw ApiProblemException.Validation("file", "必须且只能上传一个 file 文件字段。");
            }

            if (file.Length == 0)
            {
                throw ApiProblemException.Validation("file", "请选择非空图片文件。");
            }

            if (file.Length > MediaLimits.MaxImageBytes)
            {
                throw ApiProblemException.UploadTooLarge(MediaLimits.MaxImageBytes);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            await using var stream = file.OpenReadStream();
            var result = await images.UploadAsync(
                actorId,
                stream,
                file.Length,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Json(new { data = result }, statusCode: StatusCodes.Status201Created);
        });
    }
}