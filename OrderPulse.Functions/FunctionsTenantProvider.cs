using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Functions;

/// <summary>
/// Tenant provider for Azure Functions context.
/// Returns Guid.Empty since Functions operate across all tenants
/// (e.g., polling all tenant mailboxes). The Tenants table has no
/// RLS policy, so queries against it work normally. For tenant-specific
/// queries, the code uses IgnoreQueryFilters() and filters manually.
/// </summary>
public class FunctionsTenantProvider : ITenantProvider
{
    public Guid GetTenantId() => Guid.Empty;
}
