using System.Security.Claims;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

internal sealed class AgentScopeFilter(string scope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!http.User.FindAll("scope").Any(claim => claim.Value == scope))
        {
            throw new ApiProblemException(
                ApiErrorCodes.ScopeDenied,
                403,
                "服务账号 scope 不足。",
                new { requiredScope = scope });
        }

        return await next(context);
    }
}

internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        var agent = app.MapGroup("/api/v1/agent")
            .RequireAuthorization("AgentService");

        agent.MapGet("/moderation/tasks", async (
            string? status,
            string? limit,
            string? cursor,
            HttpContext http,
            AgentModerationService service) =>
        {
            var identity = RequireIdentity(http);
            var parsedLimit = ParseLimit(limit);
            var page = await service.ListTasksAsync(
                identity,
                InstanceHeader(http),
                status,
                parsedLimit,
                cursor,
                http.RequestAborted);
            return Results.Ok(new { data = page });
        })
            .AddEndpointFilter(new AgentScopeFilter("moderation:read"))
            .RequireRateLimiting("agent-read");

        agent.MapGet("/moderation/tasks/{taskId:guid}", async (
            Guid taskId,
            HttpContext http,
            AgentModerationService service) =>
        {
            var identity = RequireIdentity(http);
            var task = await service.GetTaskAsync(
                identity,
                InstanceHeader(http),
                taskId,
                http.RequestAborted);
            return Results.Ok(new { data = task });
        })
            .AddEndpointFilter(new AgentScopeFilter("moderation:read"))
            .RequireRateLimiting("agent-read");

        agent.MapPost("/moderation/tasks/{taskId:guid}/claim", async (
            Guid taskId,
            HttpContext http,
            AgentModerationService service,
            ApiAuditContextFactory auditContextFactory) =>
        {
            var identity = RequireIdentity(http);
            var result = await service.ClaimTaskAsync(
                identity,
                InstanceHeader(http),
                taskId,
                auditContextFactory.Create(identity.ServiceAccountId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        })
            .AddEndpointFilter(new AgentScopeFilter("moderation:suggest"))
            .RequireRateLimiting("agent-write");

        agent.MapPost("/moderation/tasks/{taskId:guid}/suggestion", async (
            Guid taskId,
            HttpContext http,
            AgentModerationService service,
            AgentFailureRecorder failures,
            ApiAuditContextFactory auditContextFactory) =>
        {
            var identity = RequireIdentity(http);
            var audit = auditContextFactory.Create(identity.ServiceAccountId);
            AgentSuggestionRequest request;
            try
            {
                request = await EndpointHelpers.ReadJsonAsync<AgentSuggestionRequest>(http);
            }
            catch (ApiProblemException exception) when (exception.Code == ApiErrorCodes.ValidationFailed)
            {
                await failures.RecordSuggestionInvalidAsync(
                    identity.ServiceAccountId,
                    taskId,
                    ApiErrorCodes.ValidationFailed,
                    audit,
                    http.RequestAborted);
                throw;
            }

            try
            {
                var (result, replayed) = await service.SubmitSuggestionAsync(
                    identity,
                    InstanceHeader(http),
                    taskId,
                    request,
                    audit,
                    http.RequestAborted);
                return Results.Json(
                    new { data = result },
                    statusCode: replayed
                        ? StatusCodes.Status200OK
                        : StatusCodes.Status201Created);
            }
            catch (ApiProblemException exception) when (exception.Code == ApiErrorCodes.SuggestionInvalid)
            {
                await failures.RecordSuggestionInvalidAsync(
                    identity.ServiceAccountId,
                    taskId,
                    ApiErrorCodes.SuggestionInvalid,
                    audit,
                    http.RequestAborted);
                throw;
            }
        })
            .AddEndpointFilter(new AgentScopeFilter("moderation:suggest"))
            .RequireRateLimiting("agent-write");

        agent.MapPost("/moderation/tasks/{taskId:guid}/release", async (
            Guid taskId,
            HttpContext http,
            AgentModerationService service,
            ApiAuditContextFactory auditContextFactory) =>
        {
            var identity = RequireIdentity(http);
            var request = await ReadOptionalJsonAsync<AgentReleaseRequest>(http);
            var result = await service.ReleaseTaskAsync(
                identity,
                InstanceHeader(http),
                taskId,
                request,
                auditContextFactory.Create(identity.ServiceAccountId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        })
            .AddEndpointFilter(new AgentScopeFilter("moderation:suggest"))
            .RequireRateLimiting("agent-write");

        agent.MapPost("/heartbeat", async (
            HttpContext http,
            AgentModerationService service,
            ApiAuditContextFactory auditContextFactory) =>
        {
            var identity = RequireIdentity(http);
            var request = await EndpointHelpers.ReadJsonAsync<AgentHeartbeatRequest>(http);
            var result = await service.HeartbeatAsync(
                identity,
                request,
                auditContextFactory.Create(identity.ServiceAccountId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        })
            .AddEndpointFilter(new AgentScopeFilter("agent:heartbeat"))
            .RequireRateLimiting("agent-heartbeat");

        agent.MapGet("/moderation/policy", async (
            HttpContext http,
            AgentModerationService service) =>
        {
            _ = RequireIdentity(http);
            var policy = await service.GetPolicyAsync(http.RequestAborted);
            return Results.Ok(new { data = policy });
        })
            .AddEndpointFilter(new AgentScopeFilter("moderation:read"))
            .RequireRateLimiting("agent-read");
    }

    private static AgentIdentity RequireIdentity(HttpContext http)
    {
        if (http.Items.TryGetValue("AgentIdentity", out var value)
            && value is AgentIdentity identity)
        {
            return identity;
        }

        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId))
        {
            throw new ApiProblemException(
                ApiErrorCodes.ServiceTokenInvalid,
                401,
                "服务令牌无效。");
        }

        return new AgentIdentity(
            accountId,
            http.User.Identity?.Name ?? string.Empty,
            http.User.FindAll("scope").Select(claim => claim.Value).ToList());
    }

    private static string? InstanceHeader(HttpContext http)
    {
        return http.Request.Headers["X-Agent-Instance-Id"].ToString();
    }

    private static int ParseLimit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 20;
        }

        if (!int.TryParse(raw, out var limit) || limit is < 1 or > 50)
        {
            throw ApiProblemException.Validation("limit", "limit 必须是 1 到 50 之间的整数。");
        }

        return limit;
    }

    private static async Task<T?> ReadOptionalJsonAsync<T>(HttpContext http)
        where T : class
    {
        if (http.Request.ContentLength is 0 or null && !http.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return null;
        }

        return await EndpointHelpers.ReadJsonAsync<T>(http);
    }
}