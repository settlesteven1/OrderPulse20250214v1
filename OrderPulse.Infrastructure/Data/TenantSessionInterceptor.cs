using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OrderPulse.Infrastructure.Data;

/// <summary>
/// EF Core connection interceptor that sets SESSION_CONTEXT('TenantId')
/// on every database connection. This activates the Row-Level Security
/// policies in Azure SQL, providing defense-in-depth tenant isolation.
/// </summary>
public class TenantSessionInterceptor : DbConnectionInterceptor
{
    private readonly ITenantProvider _tenantProvider;

    public TenantSessionInterceptor(ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantProvider.GetTenantId();
        if (tenantId != Guid.Empty)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId";
            var param = cmd.CreateParameter();
            param.ParameterName = "@tenantId";
            param.Value = tenantId.ToString();
            cmd.Parameters.Add(param);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }
}
