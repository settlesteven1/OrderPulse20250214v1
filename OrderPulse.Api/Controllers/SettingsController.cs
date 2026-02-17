using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Api.DTOs;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly OrderPulseDbContext _db;
    private readonly ITenantProvider _tenantProvider;

    public SettingsController(OrderPulseDbContext db, ITenantProvider tenantProvider)
    {
        _db = db;
        _tenantProvider = tenantProvider;
    }

    /// <summary>
    /// Get current tenant settings.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<TenantSettingsDto>>> GetSettings(CancellationToken ct)
    {
        var tenantId = _tenantProvider.GetTenantId();
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId, ct);

        if (tenant is null)
            return NotFound(new ApiError("TENANT_NOT_FOUND", "Tenant not found."));

        var mailboxStatus = tenant.IsActive
            ? (tenant.LastSyncAt.HasValue ? "Active" : "Connected")
            : "Disconnected";

        var dto = new TenantSettingsDto(
            ConnectedEmail: tenant.PurchaseMailbox,
            LastSyncAt: tenant.LastSyncAt,
            MailboxStatus: mailboxStatus,
            // These would come from a TenantSettings table in production;
            // for now return sensible defaults
            PollingIntervalMinutes: 5,
            WebhookEnabled: !string.IsNullOrEmpty(tenant.GraphSubscriptionId),
            NotifyDelivery: true,
            NotifyShipment: true,
            NotifyReturn: true,
            NotifyRefund: true,
            NotifyIssues: true
        );

        return Ok(new ApiResponse<TenantSettingsDto>(dto));
    }

    /// <summary>
    /// Update tenant settings.
    /// </summary>
    [HttpPut]
    public async Task<ActionResult> SaveSettings([FromBody] TenantSettingsUpdateDto update, CancellationToken ct)
    {
        // In production, persist to a TenantSettings table.
        // For MVP, acknowledge the save.
        return Ok();
    }

    /// <summary>
    /// Trigger historical email import for a date range.
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<HistoricalImportResultDto>> TriggerImport(
        [FromBody] HistoricalImportRequestDto request, CancellationToken ct)
    {
        // In production, this would enqueue a Service Bus message
        // that tells the Function App to pull historical emails.
        // For MVP, return a placeholder response.
        var daySpan = (request.EndDate - request.StartDate).Days;
        var estimatedEmails = Math.Max(1, daySpan * 3); // rough estimate

        return Ok(new HistoricalImportResultDto(
            EmailsQueued: estimatedEmails,
            Message: $"Queued import for {request.StartDate:MMM d} – {request.EndDate:MMM d, yyyy}. Processing will begin shortly."
        ));
    }
}
