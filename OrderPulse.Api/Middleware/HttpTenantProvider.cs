using System.Security.Claims;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Api.Middleware;

/// <summary>
/// Extracts TenantId from the authenticated user's JWT claims.
/// The TenantId is set as a custom claim during B2C token issuance.
/// </summary>
public class HttpTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid GetTenantId()
    {
        var claim = _httpContextAccessor.HttpContext?.User?.FindFirst("extension_TenantId")
                 ?? _httpContextAccessor.HttpContext?.User?.FindFirst("tenantId")
                 ?? _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier);

        if (claim is not null && Guid.TryParse(claim.Value, out var tenantId))
            return tenantId;

        return Guid.Empty;
    }
}
