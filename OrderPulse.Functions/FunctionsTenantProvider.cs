using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Functions;

/// <summary>
/// Tenant provider for Azure Functions context.
/// Mutable so that functions can set the current tenant before
/// performing tenant-scoped DB operations (required by RLS).
/// </summary>
public class FunctionsTenantProvider : ITenantProvider
{
    private static readonly AsyncLocal<Guid> _currentTenantId = new();

    public Guid GetTenantId() => _currentTenantId.Value;

    /// <summary>
    /// Sets the current tenant for the async execution context.
    /// Must be called before any tenant-scoped DB writes.
    /// </summary>
    public static void SetCurrentTenant(Guid tenantId) => _currentTenantId.Value = tenantId;
}
