using System.Security.Claims;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Api.Services;

public sealed class AgentTokenMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ServiceAccountAdminService accounts)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiProblemException(
                ApiErrorCodes.ServiceTokenInvalid,
                401,
                "缺少服务令牌。");
        }

        var token = authorization["Bearer ".Length..].Trim();
        var identity = await accounts.AuthenticateTokenAsync(
            token,
            context.Connection.RemoteIpAddress?.ToString(),
            context.RequestAborted);
        if (identity.Status != ServiceTokenValidationStatus.Valid
            || identity.ServiceAccountId is not Guid accountId)
        {
            throw identity.Status switch
            {
                ServiceTokenValidationStatus.Expired => new ApiProblemException(
                    ApiErrorCodes.ServiceTokenExpired,
                    401,
                    "服务令牌已过期。"),
                ServiceTokenValidationStatus.Revoked => new ApiProblemException(
                    ApiErrorCodes.ServiceTokenRevoked,
                    401,
                    "服务令牌已撤销。"),
                ServiceTokenValidationStatus.AccountDisabled => new ApiProblemException(
                    ApiErrorCodes.ServiceTokenInvalid,
                    401,
                    "服务账号已停用。",
                    new { reason = "account_disabled" }),
                ServiceTokenValidationStatus.IpDenied => ApiProblemException.Forbidden(
                    "ip_denied",
                    "来源地址不在服务账号 IP allowlist 中。"),
                _ => new ApiProblemException(
                    ApiErrorCodes.ServiceTokenInvalid,
                    401,
                    "服务令牌无效。")
            };
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, accountId.ToString()),
            new(ClaimTypes.Name, identity.ServiceAccountName ?? string.Empty),
            new("auth_type", "service")
        };
        claims.AddRange(identity.Scopes.Select(scope => new Claim("scope", scope)));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ServiceBearer"));
        context.Items["AgentIdentity"] = new AgentIdentity(
            accountId,
            identity.ServiceAccountName ?? string.Empty,
            identity.Scopes);
        await next(context);
    }
}