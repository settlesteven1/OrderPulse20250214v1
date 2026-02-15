using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Functions.EmailProcessing;

/// <summary>
/// Service Bus-triggered function that classifies incoming emails.
/// Picks up from 'emails-pending' queue, runs the two-pass AI classifier,
/// and forwards classified emails to 'emails-classified' for parsing.
/// </summary>
public class EmailClassifierFunction
{
    private readonly ILogger<EmailClassifierFunction> _logger;
    private readonly OrderPulseDbContext _db;
    private readonly IEmailClassifier _classifier;

    public EmailClassifierFunction(
        ILogger<EmailClassifierFunction> logger,
        OrderPulseDbContext db,
        IEmailClassifier classifier)
    {
        _logger = logger;
        _db = db;
        _classifier = classifier;
    }

    [Function("EmailClassifierFunction")]
    [ServiceBusOutput("emails-classified", Connection = "ServiceBusConnection")]
    public async Task<string?> Run(
        [ServiceBusTrigger("emails-pending", Connection = "ServiceBusConnection")]
        string emailMessageId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(emailMessageId, out var id))
        {
            _logger.LogError("Invalid email message ID: {id}", emailMessageId);
            return null;
        }

        _logger.LogInformation("Classifying email {id}", id);

        var email = await _db.EmailMessages.FindAsync(new object[] { id }, ct);
        if (email is null)
        {
            _logger.LogWarning("Email {id} not found", id);
            return null;
        }

        try
        {
            email.ProcessingStatus = ProcessingStatus.Classifying;
            await _db.SaveChangesAsync(ct);

            // Step 1: Pre-filter (is this order-related?)
            var isOrderRelated = await _classifier.IsOrderRelatedAsync(
                email.Subject, email.BodyPreview ?? "", email.FromAddress, ct);

            if (!isOrderRelated)
            {
                email.ClassificationType = EmailClassificationType.Promotional;
                email.ClassificationConfidence = 0.95m;
                email.ProcessingStatus = ProcessingStatus.Parsed; // Skip parsing
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Email {id} classified as promotional (filtered)", id);
                return null; // Don't forward to parser
            }

            // Step 2: Full classification
            // TODO: Retrieve full body from Blob Storage using email.BodyBlobUrl
            var fullBody = email.BodyPreview ?? ""; // Placeholder
            var result = await _classifier.ClassifyAsync(
                email.Subject, fullBody, email.FromAddress, ct);

            email.ClassificationType = result.Type;
            email.ClassificationConfidence = result.Confidence;
            email.ProcessingStatus = ProcessingStatus.Classified;

            // If confidence is too low, flag for review
            if (result.Confidence < 0.7m)
            {
                email.ProcessingStatus = ProcessingStatus.ManualReview;
                await _db.SaveChangesAsync(ct);
                _logger.LogWarning("Email {id} flagged for review (confidence: {conf})", id, result.Confidence);
                return null;
            }

            await _db.SaveChangesAsync(ct);

            // Forward to parsing queue
            return id.ToString();
        }
        catch (Exception ex)
        {
            email.ProcessingStatus = ProcessingStatus.Failed;
            email.ErrorDetails = ex.Message;
            email.RetryCount++;
            await _db.SaveChangesAsync(ct);

            _logger.LogError(ex, "Failed to classify email {id}", id);
            throw; // Let Service Bus handle retry/dead-letter
        }
    }
}
