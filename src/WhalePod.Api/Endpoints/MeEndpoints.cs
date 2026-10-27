using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

internal static class MeEndpoints
{
    public static void MapMeEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/api/v1/me").RequireAuthorization();

        me.MapGet("/posts", async (
            string? status,
            string? limit,
            string? cursor,
            HttpContext http,
            AuthorContentService authorContent) =>
        {
            if (cursor?.Length > 512)
            {
                throw ApiProblemException.Validation("cursor", "cursor 过长。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await authorContent.GetMyPostsAsync(
                actorId,
                status,
                EndpointHelpers.ParseLimit(limit),
                cursor,
                http.RequestAborted);
            if (page.InvalidCursor)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效或与当前筛选不匹配。");
            }

            return Results.Ok(new { data = new { items = page.Items, nextCursor = page.NextCursor } });
        });

        me.MapGet("/comments", async (
            string? status,
            string? limit,
            string? cursor,
            HttpContext http,
            AuthorContentService authorContent) =>
        {
            if (cursor?.Length > 512)
            {
                throw ApiProblemException.Validation("cursor", "cursor 过长。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await authorContent.GetMyCommentsAsync(
                actorId,
                status,
                EndpointHelpers.ParseLimit(limit),
                cursor,
                http.RequestAborted);
            if (page.InvalidCursor)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效或与当前筛选不匹配。");
            }

            return Results.Ok(new { data = new { items = page.Items, nextCursor = page.NextCursor } });
        });
    }
}