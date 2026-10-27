using System.Security.Cryptography;
using System.Text;

using WhalePod.Infrastructure.Services;

namespace WhalePod.Web.Security;

public sealed class AuditRequestContextFactory(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
{
    private readonly string? _ipHashSecret = configuration["Audit:IpHashSecret"];

    public AuditContext Create(Guid actorId)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var address = httpContext?.Connection.RemoteIpAddress?.ToString();
        byte[]? ipHash = null;

        if (!string.IsNullOrWhiteSpace(_ipHashSecret) && !string.IsNullOrWhiteSpace(address))
        {
            ipHash = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(_ipHashSecret),
                Encoding.UTF8.GetBytes(address));
        }

        return new AuditContext(actorId, httpContext?.TraceIdentifier, ipHash);
    }
}