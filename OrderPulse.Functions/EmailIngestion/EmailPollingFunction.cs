using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Services;
using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;
using Azure.Messaging.ServiceBus;

namespace OrderPulse.Functions.EmailIngestion;

/// <summary>
/// Timer-triggered function that polls each active tenant's mailbox for new emails.
/// Runs every 5 minutes. For each new email found, stores the body in Blob Storage,
/// creates an EmailMessage record, and publishes to the Service Bus queue for processing.
/// </summary>
public class EmailPollingFunction
{
    private readonly ILogger<EmailPollingFunction> _logger;
    private readonly OrderPulseDbContext _db;
    private readonly ServiceBusClient _serviceBus;
    private readonly GraphMailService _graphMail;
    private readonly EmailBlobStorageService _blobStorage;

    public EmailPollingFunction(
        ILogger<EmailPollingFunction> logger,
        OrderPulseDbContext db,
        ServiceBusClient serviceBus,
        GraphMailService graphMail,
        EmailBlobStorageService blobStorage)
    {
        _logger = logger;
        _db = db;
        _serviceBus = serviceBus;
        _graphMail = graphMail;
        _blobStorage = blobStorage;
    }

    [Function("EmailPollingFunction")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        _logger.LogInformation("Email polling started at {time}", DateTime.UtcNow);

        // Get all active tenants
        // Note: This query bypasses RLS since the Function's TenantProvider
        // returns Guid.Empty, and the Tenants table has no RLS policy.
        var tenants = await _db.Tenants
            .Where(t => t.IsActive)
            .ToListAsync(ct);

        _logger.LogInformation("Found {count} active tenants to poll", tenants.Count);

        var sender = _serviceBus.CreateSender("emails-pending");

        foreach (var tenant in tenants)
        {
            try
            {
                await PollTenantMailboxAsync(tenant, sender, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to poll mailbox for tenant {tenantId}", tenant.TenantId);
            }
        }

        await sender.DisposeAsync();
        _logger.LogInformation("Email polling completed at {time}", DateTime.UtcNow);
    }

    private async Task PollTenantMailboxAsync(
        Tenant tenant,
        ServiceBusSender sender,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenant.PurchaseMailbox))
        {
            _logger.LogWarning("Tenant {tenantId} has no purchase mailbox configured", tenant.TenantId);
            return;
        }

        // Set the current tenant so RLS allows our DB operations
        FunctionsTenantProvider.SetCurrentTenant(tenant.TenantId);

        // Explicitly set SESSION_CONTEXT on the already-open connection
        // (the interceptor only fires on connection open, which already happened)
        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
            tenant.TenantId.ToString());

        _logger.LogInformation("Polling mailbox {mailbox} for tenant {tenantId} (since {since})",
            tenant.PurchaseMailbox, tenant.TenantId, tenant.LastSyncAt);

        // Fetch new messages from Graph API
        var messages = await _graphMail.GetNewMessagesAsync(
            tenant.PurchaseMailbox, tenant.LastSyncAt, ct);

        if (messages.Count == 0)
        {
            _logger.LogInformation("No new messages for tenant {tenantId}", tenant.TenantId);
            return;
        }

        var newCount = 0;

        foreach (var msg in messages)
        {
            if (msg.Id is null) continue;

            // Deduplication by Graph message ID
            var exists = await _db.EmailMessages
                .IgnoreQueryFilters() // Bypass tenant filter for cross-tenant dedup check
                .AnyAsync(e => e.TenantId == tenant.TenantId && e.GraphMessageId == msg.Id, ct);

            if (exists)
            {
                _logger.LogDebug("Skipping duplicate message {graphId}", msg.Id);
                continue;
            }

            try
            {
                // Store body in Blob Storage
                var bodyContent = msg.Body?.Content ?? "";
                var blobUrl = await _blobStorage.StoreEmailBodyAsync(
                    tenant.TenantId, msg.Id, bodyContent, ct);

                // Create EmailMessage record
                var email = new EmailMessage
                {
                    EmailMessageId = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    GraphMessageId = msg.Id,
                    InternetMessageId = msg.InternetMessageId,
                    FromAddress = msg.From?.EmailAddress?.Address ?? "unknown",
                    FromDisplayName = msg.From?.EmailAddress?.Name,
                    Subject = msg.Subject ?? "(no subject)",
                    ReceivedAt = msg.ReceivedDateTime?.UtcDateTime ?? DateTime.UtcNow,
                    BodyBlobUrl = blobUrl,
                    BodyPreview = TruncatePreview(msg.BodyPreview, 500),
                    HasAttachments = msg.HasAttachments ?? false,
                    ProcessingStatus = ProcessingStatus.Pending
                };

                _db.EmailMessages.Add(email);
                await _db.SaveChangesAsync(ct);

                // Publish to Service Bus for async processing
                var sbMsg = new ServiceBusMessage(email.EmailMessageId.ToString());
                sbMsg.ApplicationProperties["TenantId"] = tenant.TenantId.ToString();
                await sender.SendMessageAsync(sbMsg, ct);

                newCount++;
                _logger.LogDebug("Ingested message {graphId} as {emailId}", msg.Id, email.EmailMessageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ingest message {graphId} for tenant {tenantId}",
                    msg.Id, tenant.TenantId);
                // Continue with next message — don't let one failure block the batch
            }
        }

        // Update last sync time
        tenant.LastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Polled mailbox for tenant {tenantId}: {new} new of {total} messages",
            tenant.TenantId, newCount, messages.Count);
    }

    private static string? TruncatePreview(string? preview, int maxLength)
    {
        if (string.IsNullOrEmpty(preview)) return null;
        return preview.Length <= maxLength ? preview : preview[..maxLength];
    }
}
