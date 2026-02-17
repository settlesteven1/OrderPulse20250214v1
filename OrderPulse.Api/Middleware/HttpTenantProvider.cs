using System.Security.Claims;
using Microsoft.Data.SqlClient;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Api.Middleware;

/// <summary>
/// Extracts TenantId from the authenticated user's JWT claims.
/// First checks for explicit TenantId claims, then falls back to
/// looking up the Tenant by the user's email from the JWT.
/// Results are cached per-request in HttpContext.Items.
/// </summary>
public class HttpTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private const string CacheKey = "__ResolvedTenantId";

    public HttpTenantProvider(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    public Guid GetTenantId()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
            return Guid.Empty;

        // Check per-request cache first
        if (httpContext.Items.TryGetValue(CacheKey, out var cached) && cached is Guid cachedId)
            return cachedId;

        // Try explicit TenantId claims first
        var claim = httpContext.User?.FindFirst("extension_TenantId")
                 ?? httpContext.User?.FindFirst("tenantId");

        if (claim is not null && Guid.TryParse(claim.Value, out var tenantId))
        {
            httpContext.Items[CacheKey] = tenantId;
            return tenantId;
        }

        // Fall back: look up tenant by user's email address
        var email = httpContext.User?.FindFirst("preferred_username")?.Value
                 ?? httpContext.User?.FindFirst(ClaimTypes.Email)?.Value
                 ?? httpContext.User?.FindFirst("email")?.Value;

        if (!string.IsNullOrEmpty(email))
        {
            var resolved = LookupTenantByEmail(email);
            httpContext.Items[CacheKey] = resolved;
            return resolved;
        }

        // Last resort: try NameIdentifier (Azure AD object ID)
        var oidClaim = httpContext.User?.FindFirst(ClaimTypes.NameIdentifier);
        if (oidClaim is not null && Guid.TryParse(oidClaim.Value, out var oid))
        {
            httpContext.Items[CacheKey] = oid;
            return oid;
        }

        httpContext.Items[CacheKey] = Guid.Empty;
        return Guid.Empty;
    }

    private Guid LookupTenantByEmail(string email)
    {
        var connStr = _configuration.GetConnectionString("OrderPulseDb");
        if (string.IsNullOrEmpty(connStr))
            return Guid.Empty;

        try
        {
            using var conn = new SqlConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 TenantId FROM Tenants WHERE Email = @Email AND IsActive = 1";
            cmd.Parameters.AddWithValue("@Email", email);
            var result = cmd.ExecuteScalar();
            if (result is Guid g)
                return g;
        }
        catch
        {
            // Log in production; for now swallow to prevent startup failures
        }

        return Guid.Empty;
    }
}
