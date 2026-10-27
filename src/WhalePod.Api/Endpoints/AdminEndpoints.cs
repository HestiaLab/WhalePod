using Microsoft.AspNetCore.RateLimiting;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var reads = app.MapGroup("/api/v1/admin").RequireAuthorization();
        var writes = app.MapGroup("/api/v1/admin")
            .RequireAuthorization()
            .AddEndpointFilter<CsrfValidationFilter>()
            .RequireRateLimiting("api-write");

        reads.MapGet("/moderation/queue", async (
            string? targetType,
            string? limit,
            string? cursor,
            HttpContext http,
            ContentModerationService moderation) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await moderation.GetQueueAsync(
                actorId,
                targetType,
                EndpointHelpers.ParseLimit(limit),
                cursor,
                http.RequestAborted);
            if (page.InvalidCursor)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效或与当前筛选不匹配。");
            }

            return Results.Ok(new { data = new { items = page.Items, nextCursor = page.NextCursor } });
        });

        writes.MapPost("/moderation/{targetType}/{targetId}/action", async (
            string targetType,
            string targetId,
            HttpContext http,
            ContentModerationService moderation,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(targetId, out var parsedTargetId))
            {
                throw ApiProblemException.NotFound("待审核内容不存在或已删除。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var command = await EndpointHelpers.ReadJsonAsync<ModerationActionCommand>(http);
            var result = await moderation.ExecuteAsync(
                actorId,
                targetType,
                parsedTargetId,
                command,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        });

        reads.MapGet("/reports", async (
            string? status,
            string? limit,
            string? cursor,
            HttpContext http,
            ReportService reports) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await reports.ListAsync(
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

        writes.MapPost("/reports/{reportId}/resolve", async (
            string reportId,
            HttpContext http,
            ReportService reports,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(reportId, out var parsedReportId))
            {
                throw ApiProblemException.NotFound("举报不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var command = await EndpointHelpers.ReadJsonAsync<ReportResolveCommand>(http);
            var report = await reports.ResolveAsync(
                actorId,
                parsedReportId,
                command,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = report });
        });

        reads.MapGet("/users", async (
            string? query,
            string? status,
            string? trustLevel,
            string? limit,
            string? cursor,
            HttpContext http,
            AdminUserService users) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var page = await users.ListAsync(
                actorId,
                query,
                status,
                EndpointHelpers.ParseTrustLevel(trustLevel),
                EndpointHelpers.ParseLimit(limit),
                cursor,
                http.RequestAborted);
            if (page.InvalidCursor)
            {
                throw ApiProblemException.Validation("cursor", "cursor 无效或与当前筛选不匹配。");
            }

            return Results.Ok(new { data = new { items = page.Items, nextCursor = page.NextCursor } });
        });

        reads.MapGet("/users/{userId}", async (
            string userId,
            HttpContext http,
            AdminUserService users) =>
        {
            if (!Guid.TryParse(userId, out var parsedUserId))
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var user = await users.GetAsync(actorId, parsedUserId, http.RequestAborted);
            return Results.Ok(new { data = user });
        });

        writes.MapPatch("/users/{userId}", async (
            string userId,
            HttpContext http,
            AdminUserService users,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(userId, out var parsedUserId))
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var request = await EndpointHelpers.ReadJsonAsync<AdminTrustLevelRequest>(http);
            if (request.TrustLevel is null or < 0 or > 4)
            {
                throw ApiProblemException.Validation("trustLevel", "trustLevel 必须在 0–4 之间。");
            }

            var user = await users.ChangeTrustLevelAsync(
                actorId,
                parsedUserId,
                (short)request.TrustLevel.Value,
                request.Reason,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = user });
        });

        writes.MapPost("/users/{userId}/status", async (
            string userId,
            HttpContext http,
            AdminUserService users,
            ApiAuditContextFactory auditFactory) =>
        {
            if (!Guid.TryParse(userId, out var parsedUserId))
            {
                throw ApiProblemException.NotFound("用户不存在。");
            }

            var actorId = EndpointHelpers.RequireActorId(http);
            var request = await EndpointHelpers.ReadJsonAsync<AdminUserStatusRequest>(http);
            var user = await users.ChangeStatusAsync(
                actorId,
                parsedUserId,
                request.Status,
                request.Reason,
                request.ExpiresAt,
                auditFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = user });
        });
    }

    private sealed record AdminTrustLevelRequest(int? TrustLevel, string? Reason);

    private sealed record AdminUserStatusRequest(
        string? Status,
        string? Reason,
        DateTimeOffset? ExpiresAt);
}