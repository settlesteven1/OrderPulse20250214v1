using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.AI;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Services;

namespace OrderPulse.Functions.EmailProcessing;

/// <summary>
/// Timer-triggered function that processes unprocessed emails in chronological order.
/// Runs every 2 minutes. Picks up emails in Pending or Classified status,
/// ordered by ReceivedAt ascending (oldest first), and processes them one at a time
/// through classification and parsing.
///
/// This ensures order confirmations are processed before their shipment/delivery emails,
/// preventing orphaned stub orders and incorrect status calculations.
/// </summary>
public class EmailProcessingBatchFunction
{
    private readonly ILogger<EmailProcessingBatchFunction> _logger;
    private readonly OrderPulseDbContext _db;
    private readonly IEmailClassifier _classifier;
    private readonly IEmailProcessingOrchestrator _orchestrator;
    private readonly EmailBlobStorageService _blobStorage;

    /// <summary>Maximum emails to process per run (to stay within function timeout).</summary>
    private const int MaxPerRun = 20;

    public EmailProcessingBatchFunction(
        ILogger<EmailProcessingBatchFunction> logger,
        OrderPulseDbContext db,
        IEmailClassifier classifier,
        IEmailProcessingOrchestrator orchestrator,
        EmailBlobStorageService blobStorage)
    {
        _logger = logger;
        _db = db;
        _classifier = classifier;
        _orchestrator = orchestrator;
        _blobStorage = blobStorage;
    }

    [Function("EmailProcessingBatchFunction")]
    public async Task Run(
        [TimerTrigger("0 */2 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        // Find unprocessed emails: Pending (needs classification) or Classified (needs parsing)
        // Ordered by ReceivedAt ascending so older emails process first
        var emails = await _db.EmailMessages
            .IgnoreQueryFilters()
            .Where(e => e.ProcessingStatus == ProcessingStatus.Pending
                     || e.ProcessingStatus == ProcessingStatus.Classified)
            .OrderBy(e => e.ReceivedAt)
            .Take(MaxPerRun)
            .ToListAsync(ct);

        if (emails.Count == 0)
            return;

        _logger.LogInformation(
            "Processing {count} emails in chronological order (oldest: {oldest}, newest: {newest})",
            emails.Count,
            emails.First().ReceivedAt,
            emails.Last().ReceivedAt);

        var classified = 0;
        var parsed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var email in emails)
        {
            try
            {
                // Set tenant context for each email
                FunctionsTenantProvider.SetCurrentTenant(email.TenantId);
                await _db.Database.ExecuteSqlRawAsync(
                    "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
                    email.TenantId.ToString());

                if (email.ProcessingStatus == ProcessingStatus.Pending)
                {
                    // Step 1: Classify
                    var shouldParse = await ClassifyEmailAsync(email, ct);
                    if (!shouldParse)
                    {
                        skipped++;
                        continue;
                    }
                    classified++;
                }

                // Step 2: Parse (email is now Classified)
                if (email.ProcessingStatus == ProcessingStatus.Classified)
                {
                    await ParseEmailAsync(email, ct);
                    parsed++;
                }
            }
            catch (ContentFilterException cfEx)
            {
                email.ProcessingStatus = ProcessingStatus.ManualReview;
                email.ErrorDetails = $"Content filter: {cfEx.Message}";
                await _db.SaveChangesAsync(ct);
                _logger.LogWarning(cfEx, "Email {id} flagged for review — content filter", email.EmailMessageId);
                failed++;
            }
            catch (Exception ex)
            {
                email.ProcessingStatus = ProcessingStatus.Failed;
                email.ErrorDetails = ex.Message;
                email.RetryCount++;
                await _db.SaveChangesAsync(ct);
                _logger.LogError(ex, "Failed to process email {id}: {subject}",
                    email.EmailMessageId, email.Subject);
                failed++;
            }
        }

        _logger.LogInformation(
            "Batch complete: {classified} classified, {parsed} parsed, {skipped} skipped, {failed} failed",
            classified, parsed, skipped, failed);
    }

    /// <summary>
    /// Classifies an email using the two-pass AI classifier.
    /// Returns true if the email should proceed to parsing, false if it was filtered out.
    /// </summary>
    private async Task<bool> ClassifyEmailAsync(
        Domain.Entities.EmailMessage email, CancellationToken ct)
    {
        email.ProcessingStatus = ProcessingStatus.Classifying;
        await _db.SaveChangesAsync(ct);

        // Pre-filter
        var isOrderRelated = await _classifier.IsOrderRelatedAsync(
            email.Subject, email.BodyPreview ?? "", email.FromAddress, ct);

        if (!isOrderRelated)
        {
            email.ClassificationType = EmailClassificationType.Promotional;
            email.ClassificationConfidence = 0.95m;
            email.ProcessingStatus = ProcessingStatus.Parsed;
            await _db.SaveChangesAsync(ct);
            _logger.LogDebug("Email {id} filtered as promotional", email.EmailMessageId);
            return false;
        }

        // Full classification
        var fullBody = email.BodyPreview ?? "";
        if (!string.IsNullOrEmpty(email.BodyBlobUrl))
        {
            try
            {
                var blobBody = await _blobStorage.GetEmailBodyAsync(email.BodyBlobUrl, ct);
                if (!string.IsNullOrEmpty(blobBody))
                    fullBody = ForwardedEmailHelper.ExtractOriginalBody(blobBody);
            }
            catch (Exception blobEx)
            {
                _logger.LogWarning(blobEx, "Blob fetch failed for {id}, using preview", email.EmailMessageId);
            }
        }

        var result = await _classifier.ClassifyAsync(
            email.Subject, fullBody, email.FromAddress, ct);

        email.ClassificationType = result.Type;
        email.ClassificationConfidence = result.Confidence;
        email.ProcessingStatus = ProcessingStatus.Classified;

        // ServicePayment — skip parsing
        if (result.Type == EmailClassificationType.ServicePayment)
        {
            email.ProcessingStatus = ProcessingStatus.Parsed;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Email {id} classified as ServicePayment (skipping)", email.EmailMessageId);
            return false;
        }

        // Low confidence — flag for review
        if (result.Confidence < 0.7m)
        {
            email.ProcessingStatus = ProcessingStatus.ManualReview;
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning("Email {id} flagged for review (confidence: {conf})",
                email.EmailMessageId, result.Confidence);
            return false;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Parses a classified email through the processing orchestrator.
    /// </summary>
    private async Task ParseEmailAsync(
        Domain.Entities.EmailMessage email, CancellationToken ct)
    {
        await _orchestrator.ProcessEmailAsync(email.EmailMessageId, ct);
        _logger.LogInformation("Parsed email {id}: {subject}", email.EmailMessageId, email.Subject);
    }
}
