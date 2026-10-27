using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Web.Security;

public sealed class AdminRequirement : IAuthorizationRequirement;

public sealed class AdminAuthorizationHandler(AdminAccessService adminAccess) : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return;
        }

        if (await adminAccess.IsAdminAsync(userId))
        {
            context.Succeed(requirement);
        }
    }
}