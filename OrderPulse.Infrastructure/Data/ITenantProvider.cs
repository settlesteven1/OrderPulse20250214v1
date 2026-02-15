namespace OrderPulse.Infrastructure.Data;

/// <summary>
/// Provides the current tenant context. Implemented differently per host:
/// - In the API: extracted from the JWT Bearer token claims
/// - In Azure Functions: extracted from the message metadata or looked up from tenant config
/// </summary>
public interface ITenantProvider
{
    Guid GetTenantId();
}
