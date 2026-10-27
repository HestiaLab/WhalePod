using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

internal static class ContentEndpoints
{
    public static void MapContentWriteEndpoints(this WebApplication app)
    {
        var root = app.MapGroup("/api/v1");
        root.MapGet("/auth/csrf", GetCsrfToken).RequireAuthorization();

        var writes = app.MapGroup("/api/v1")
            .RequireAuthorization()
            .AddEndpointFilter<CsrfValidationFilter>()
            .RequireRateLimiting("api-write");

        writes.MapPost("/posts", async (
            HttpContext http,
            PostWriteService posts,
            ApiAuditContextFactory auditFactory) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var command = await EndpointHelpers.ReadJsonAsync<PostCreateCommand>(http);
            var view = await posts.CreateAsync(actorId, command, auditFactory.Create(actorId), http.RequestAborted);
            http.Response.Headers.ETag = $"\"v{view.Version}\"";
            return Results.Json(new { data = view }, statusCode: StatusCodes.Status201Created);
        });

        writes.MapPatch("/posts/{postId}", async (
            string postId,
            HttpContext http,
            PostWriteService posts,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(postId, out var parsedPostId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "帖子不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var version = EndpointHelpers.ParseVersion(http.Request.Headers.IfMatch);
            var command = await EndpointHelpers.ReadJsonAsync<PostUpdateCommand>(http);
            var view = await posts.UpdateAsync(
                actorId,
                parsedPostId,
                version,
                command,
                auditFactory.Create(actorId),
                http.RequestAborted);
            http.Response.Headers.ETag = $"\"v{view.Version}\"";
            return Results.Ok(new { data = view });
        });

        writes.MapDelete("/posts/{postId}", async (
            string postId,
            HttpContext http,
            PostWriteService posts,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(postId, out var parsedPostId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "帖子不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var deletedId = await posts.DeleteAsync(
                actorId,
                parsedPostId,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = new { id = deletedId, deleted = true } });
        });

        writes.MapPost("/posts/{postId}/comments", async (
            string postId,
            HttpContext http,
            CommentWriteService comments,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(postId, out var parsedPostId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "帖子不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var command = await EndpointHelpers.ReadJsonAsync<CommentCreateCommand>(http);
            var view = await comments.CreateAsync(
                actorId,
                parsedPostId,
                command,
                auditFactory.Create(actorId),
                http.RequestAborted);
            http.Response.Headers.ETag = $"\"v{view.Version}\"";
            return Results.Json(new { data = view }, statusCode: StatusCodes.Status201Created);
        });

        writes.MapPatch("/comments/{commentId}", async (
            string commentId,
            HttpContext http,
            CommentWriteService comments,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(commentId, out var parsedCommentId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "评论不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var version = EndpointHelpers.ParseVersion(http.Request.Headers.IfMatch);
            var body = await EndpointHelpers.ReadJsonAsync<CommentBodyRequest>(http);
            var reason = EndpointHelpers.NormalizeOptional(http.Request.Query["reason"].ToString());
            var command = new CommentUpdateCommand(body.BodyMd, reason);
            var view = await comments.UpdateAsync(
                actorId,
                parsedCommentId,
                version,
                command,
                auditFactory.Create(actorId),
                http.RequestAborted);
            http.Response.Headers.ETag = $"\"v{view.Version}\"";
            return Results.Ok(new { data = view });
        });

        writes.MapDelete("/comments/{commentId}", async (
            string commentId,
            HttpContext http,
            CommentWriteService comments,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(commentId, out var parsedCommentId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "评论不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var version = EndpointHelpers.ParseVersion(http.Request.Headers.IfMatch);
            var reason = EndpointHelpers.NormalizeOptional(http.Request.Query["reason"].ToString());
            var result = await comments.DeleteAsync(
                actorId,
                parsedCommentId,
                version,
                reason,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        });

        writes.MapPost("/posts/{postId}/like", async (
            string postId,
            HttpContext http,
            LikeService likes,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(postId, out var parsedPostId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "帖子不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var body = await EndpointHelpers.ReadJsonAsync<LikeRequest>(http);
            if (body.Liked is null)
            {
                throw ApiProblemException.Validation("liked", "必须提供 liked 布尔值。");
            }

            var state = await likes.SetPostLikeAsync(
                actorId,
                parsedPostId,
                body.Liked.Value,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = state });
        });

        writes.MapPost("/comments/{commentId}/like", async (
            string commentId,
            HttpContext http,
            LikeService likes,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(commentId, out var parsedCommentId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "评论不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var body = await EndpointHelpers.ReadJsonAsync<LikeRequest>(http);
            if (body.Liked is null)
            {
                throw ApiProblemException.Validation("liked", "必须提供 liked 布尔值。");
            }

            var state = await likes.SetCommentLikeAsync(
                actorId,
                parsedCommentId,
                body.Liked.Value,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = state });
        });

        writes.MapPost("/comments/{commentId}/accept", async (
            string commentId,
            HttpContext http,
            AcceptanceService acceptance,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(commentId, out var parsedCommentId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "评论不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var result = await acceptance.AcceptAsync(
                actorId,
                parsedCommentId,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        });

        writes.MapDelete("/comments/{commentId}/accept", async (
            string commentId,
            HttpContext http,
            AcceptanceService acceptance,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(commentId, out var parsedCommentId))
            {
                return Results.Json(
                    new { code = ApiErrorCodes.NotFound, message = "评论不存在或不可见。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var result = await acceptance.UnacceptAsync(
                actorId,
                parsedCommentId,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        });

        writes.MapPost("/reports", async (
            HttpContext http,
            ReportService reports,
            ApiAuditContextFactory auditFactory) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var command = await EndpointHelpers.ReadJsonAsync<ReportCreateCommand>(http);
            var receipt = await reports.CreateAsync(
                actorId,
                command,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Json(new { data = receipt }, statusCode: StatusCodes.Status201Created);
        });
    }

    private static async Task<IResult> GetCsrfToken(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            data = new
            {
                token = tokens.RequestToken,
                headerName = "X-CSRF-Token",
                cookieName = "whalepod_csrf"
            }
        });
    }

    private sealed record CommentBodyRequest(string? BodyMd);

    private sealed record LikeRequest(bool? Liked);
}