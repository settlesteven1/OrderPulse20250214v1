using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Infrastructure.Data;
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
    // private readonly GraphServiceClient _graphClient; // TODO: inject configured Graph client

    public EmailPollingFunction(
        ILogger<EmailPollingFunction> logger,
        OrderPulseDbContext db,
        ServiceBusClient serviceBus)
    {
        _logger = logger;
        _db = db;
        _serviceBus = serviceBus;
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
        // TODO: Use Graph API to fetch new messages since tenant.LastSyncAt
        //
        // var messages = await _graphClient.Users[tenant.PurchaseMailbox]
        //     .Messages
        //     .GetAsync(config =>
        //     {
        //         config.QueryParameters.Filter =
        //             $"receivedDateTime ge {tenant.LastSyncAt:yyyy-MM-ddTHH:mm:ssZ}";
        //         config.QueryParameters.Select = new[]
        //         {
        //             "id", "from", "subject", "receivedDateTime",
        //             "body", "bodyPreview", "hasAttachments", "internetMessageId"
        //         };
        //         config.QueryParameters.Top = 50;
        //         config.QueryParameters.Orderby = new[] { "receivedDateTime asc" };
        //     }, ct);
        //
        // foreach (var msg in messages.Value)
        // {
        //     // Check for duplicates
        //     var exists = await _db.EmailMessages
        //         .IgnoreQueryFilters() // Need to bypass tenant filter here
        //         .AnyAsync(e => e.TenantId == tenant.TenantId && e.GraphMessageId == msg.Id, ct);
        //     if (exists) continue;
        //
        //     // Store body in Blob Storage
        //     var blobUrl = await StoreBlobAsync(tenant.TenantId, msg.Id, msg.Body.Content, ct);
        //
        //     // Create EmailMessage record
        //     var email = new EmailMessage
        //     {
        //         TenantId = tenant.TenantId,
        //         GraphMessageId = msg.Id,
        //         InternetMessageId = msg.InternetMessageId,
        //         FromAddress = msg.From.EmailAddress.Address,
        //         FromDisplayName = msg.From.EmailAddress.Name,
        //         Subject = msg.Subject,
        //         ReceivedAt = msg.ReceivedDateTime.Value.UtcDateTime,
        //         BodyBlobUrl = blobUrl,
        //         BodyPreview = msg.BodyPreview?.Substring(0, Math.Min(500, msg.BodyPreview.Length)),
        //         HasAttachments = msg.HasAttachments ?? false,
        //         ProcessingStatus = ProcessingStatus.Pending
        //     };
        //     _db.EmailMessages.Add(email);
        //     await _db.SaveChangesAsync(ct);
        //
        //     // Publish to Service Bus for async processing
        //     var sbMsg = new ServiceBusMessage(email.EmailMessageId.ToString());
        //     sbMsg.ApplicationProperties["TenantId"] = tenant.TenantId.ToString();
        //     await sender.SendMessageAsync(sbMsg, ct);
        // }
        //
        // // Update last sync time
        // tenant.LastSyncAt = DateTime.UtcNow;
        // await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Polled mailbox for tenant {tenantId}: {mailbox}",
            tenant.TenantId, tenant.PurchaseMailbox);
    }
}
