using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph.Models;
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

                // Extract original sender from forwarded email headers/body
                var originalFrom = ExtractOriginalSender(msg);

                // Create EmailMessage record
                var email = new EmailMessage
                {
                    EmailMessageId = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    GraphMessageId = msg.Id,
                    InternetMessageId = msg.InternetMessageId,
                    FromAddress = msg.From?.EmailAddress?.Address ?? "unknown",
                    FromDisplayName = msg.From?.EmailAddress?.Name,
                    OriginalFromAddress = originalFrom,
                    Subject = msg.Subject ?? "(no subject)",
                    ReceivedAt = msg.ReceivedDateTime?.UtcDateTime ?? DateTime.UtcNow,
                    BodyBlobUrl = blobUrl,
                    BodyPreview = TruncatePreview(msg.BodyPreview, 500),
                    HasAttachments = msg.HasAttachments ?? false,
                    ProcessingStatus = ProcessingStatus.Pending
                };

                if (originalFrom is not null)
                {
                    _logger.LogInformation(
                        "Detected forwarded email for {graphId}: original sender {original} (from: {from})",
                        msg.Id, originalFrom, email.FromAddress);
                }

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

    /// <summary>
    /// Extracts the original sender email from a forwarded message.
    /// Checks internet message headers first (X-Forwarded-From, X-Original-From),
    /// then falls back to parsing common forwarded-message patterns in the body.
    /// Returns null if the email does not appear to be forwarded.
    /// </summary>
    private static string? ExtractOriginalSender(Message msg)
    {
        // Strategy 1: Check internet message headers for forwarded-from indicators
        if (msg.InternetMessageHeaders is { Count: > 0 })
        {
            var forwardedHeaders = new[] { "X-Forwarded-From", "X-Original-From", "X-Original-Sender" };
            foreach (var headerName in forwardedHeaders)
            {
                var header = msg.InternetMessageHeaders
                    .FirstOrDefault(h => string.Equals(h.Name, headerName, StringComparison.OrdinalIgnoreCase));
                if (header?.Value is not null)
                {
                    var extracted = ExtractEmailFromString(header.Value);
                    if (extracted is not null)
                        return extracted;
                }
            }
        }

        // Strategy 2: Parse common forwarded message patterns in the body
        var body = msg.Body?.Content;
        if (string.IsNullOrEmpty(body))
            return null;

        return ExtractOriginalSenderFromBody(body);
    }

    /// <summary>
    /// Parses common forwarded-email body patterns to find the original sender.
    /// Handles Gmail, Outlook, and generic forwarding formats.
    /// </summary>
    private static string? ExtractOriginalSenderFromBody(string body)
    {
        // Pattern 1: Gmail-style "---------- Forwarded message ---------\nFrom: Name <email@domain.com>"
        // Pattern 2: Outlook-style "From: Name <email@domain.com>" after forwarding separator
        // Pattern 3: Generic "From: email@domain.com"

        // Match "From:" lines that follow a forwarding indicator
        var forwardIndicators = new[]
        {
            @"[-]{3,}\s*Forwarded\s+message\s*[-]{3,}",   // Gmail
            @"Begin\s+forwarded\s+message:",                // Apple Mail
            @"_{3,}\s*\r?\n\s*From:",                       // Outlook desktop
            @"Original\s+Message",                          // Generic
        };

        foreach (var indicator in forwardIndicators)
        {
            var indicatorMatch = Regex.Match(body, indicator, RegexOptions.IgnoreCase);
            if (!indicatorMatch.Success)
                continue;

            // Look for "From:" within 500 chars after the indicator
            var searchStart = indicatorMatch.Index + indicatorMatch.Length;
            var searchRegion = body.Substring(searchStart, Math.Min(500, body.Length - searchStart));

            // Match "From:" followed by an email address (with or without display name)
            var fromMatch = Regex.Match(searchRegion,
                @"From:\s*(?:.*?<([^>]+@[^>]+)>|([A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}))",
                RegexOptions.IgnoreCase);

            if (fromMatch.Success)
            {
                return fromMatch.Groups[1].Success ? fromMatch.Groups[1].Value.Trim()
                     : fromMatch.Groups[2].Value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts an email address from a string that may contain a display name.
    /// E.g., "Amazon.com &lt;auto-confirm@amazon.com&gt;" → "auto-confirm@amazon.com"
    /// </summary>
    private static string? ExtractEmailFromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Try angle bracket format: "Name <email>"
        var angleMatch = Regex.Match(value, @"<([^>]+@[^>]+)>");
        if (angleMatch.Success)
            return angleMatch.Groups[1].Value.Trim();

        // Try bare email
        var bareMatch = Regex.Match(value, @"([A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})");
        if (bareMatch.Success)
            return bareMatch.Groups[1].Value.Trim();

        return null;
    }
}
