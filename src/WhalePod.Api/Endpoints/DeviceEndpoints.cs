using WhalePod.Contracts.Devices;
using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Endpoints;

internal static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var devices = app.MapGroup("/api/v1/devices").RequireAuthorization();

        devices.MapPost("/challenge", async (
            HttpContext http,
            DeviceVerificationService deviceVerification,
            ApiAuditContextFactory auditContextFactory) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var challenge = await deviceVerification.CreateChallengeAsync(
                actorId,
                auditContextFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = challenge });
        })
            .AddEndpointFilter<CsrfValidationFilter>()
            .RequireRateLimiting("device-challenge");

        devices.MapPost("/complete", async (
            HttpContext http,
            DeviceVerificationService deviceVerification,
            ApiAuditContextFactory auditContextFactory) =>
        {
            var request = await EndpointHelpers.ReadJsonAsync<DeviceCompleteRequest>(http);
            var actorId = EndpointHelpers.RequireActorId(http);
            var result = await deviceVerification.CompleteAsync(
                actorId,
                request,
                auditContextFactory.Create(actorId),
                http.RequestAborted);
            return Results.Created($"/api/v1/devices/{result.Device.Id}", new { data = result });
        })
            .AddEndpointFilter<CsrfValidationFilter>()
            .RequireRateLimiting("device-complete");

        devices.MapGet("", async (
            HttpContext http,
            DeviceVerificationService deviceVerification) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            return Results.Ok(new
            {
                data = await deviceVerification.ListAsync(actorId, http.RequestAborted)
            });
        });

        devices.MapDelete("/{deviceId:guid}", async (
            Guid deviceId,
            string? reason,
            HttpContext http,
            DeviceVerificationService deviceVerification,
            ApiAuditContextFactory auditContextFactory) =>
        {
            var actorId = EndpointHelpers.RequireActorId(http);
            var result = await deviceVerification.RevokeAsync(
                actorId,
                deviceId,
                reason,
                auditContextFactory.Create(actorId),
                http.RequestAborted);
            return Results.Ok(new { data = result });
        })
            .AddEndpointFilter<CsrfValidationFilter>()
            .RequireRateLimiting("device-write");
    }
}