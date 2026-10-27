using Microsoft.AspNetCore.RateLimiting;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

internal static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this WebApplication app)
    {
        var reads = app.MapGroup("/api/v1").RequireAuthorization();
        reads.MapGet("/notifications", async (
            string? filter,
            string? limit,
            string? cursor,
            HttpContext http,
            NotificationService notifications) =>
        {
            if (cursor?.Length > 512)
            {
                throw ApiProblemException.Validation("cursor", "cursor 过长。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await notifications.ListAsync(
                actorId,
                filter,
                EndpointHelpers.ParseLimit(limit),
                cursor,
                http.RequestAborted);
            if (page.InvalidCursor)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效或与当前筛选不匹配。");
            }

            return Results.Ok(new
            {
                data = new
                {
                    items = page.Items,
                    nextCursor = page.NextCursor,
                    unreadCount = page.UnreadCount
                }
            });
        });

        var writes = app.MapGroup("/api/v1")
            .RequireAuthorization()
            .AddEndpointFilter<CsrfValidationFilter>()
            .RequireRateLimiting("api-write");

        writes.MapPost("/notifications/{notificationId}/read", async (
            string notificationId,
            HttpContext http,
            NotificationService notifications) =>
        {
            if (!Guid.TryParse(notificationId, out var parsedNotificationId))
            {
                throw ApiProblemException.NotFound("通知不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var result = await notifications.MarkReadAsync(
                actorId,
                parsedNotificationId,
                http.RequestAborted);
            return Results.Ok(new { data = result });
        });

        writes.MapPost("/notifications/read-all", async (
            HttpContext http,
            NotificationService notifications) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var result = await notifications.MarkAllReadAsync(actorId, http.RequestAborted);
            return Results.Ok(new { data = result });
        });
    }
}