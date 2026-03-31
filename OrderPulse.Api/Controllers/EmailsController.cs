using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Api.DTOs;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.AI.Parsers;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Services;

namespace OrderPulse.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EmailsController : ControllerBase
{
    private readonly IEmailMessageRepository _emailRepo;
    private readonly IEmailProcessingOrchestrator _orchestrator;
    private readonly IEmailClassifier _classifier;
    private readonly OrderPulseDbContext _db;
    private readonly EmailBlobStorageService _blobStorage;
    private readonly IEmailParser<OrderParserResult> _orderParser;
    private readonly IEmailParser<DeliveryParserResult> _deliveryParser;
    private readonly OrderStateMachine _stateMachine;

    public EmailsController(
        IEmailMessageRepository emailRepo,
        IEmailProcessingOrchestrator orchestrator,
        IEmailClassifier classifier,
        OrderPulseDbContext db,
        EmailBlobStorageService blobStorage,
        IEmailParser<OrderParserResult> orderParser,
        IEmailParser<DeliveryParserResult> deliveryParser,
        OrderStateMachine stateMachine)
    {
        _emailRepo = emailRepo;
        _orchestrator = orchestrator;
        _classifier = classifier;
        _db = db;
        _blobStorage = blobStorage;
        _orderParser = orderParser;
        _deliveryParser = deliveryParser;
        _stateMachine = stateMachine;
    }

    /// <summary>
    /// Get emails in the manual review queue (low confidence or failed parsing).
    /// </summary>
    [HttpGet("review-queue")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ReviewQueueItemDto>>>> GetReviewQueue(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await _emailRepo.GetReviewQueueAsync(
            Math.Max(1, page), Math.Clamp(pageSize, 1, 100), ct);

        var dtos = items.Select(e => new ReviewQueueItemDto(
            e.EmailMessageId,
            e.FromAddress,
            e.FromDisplayName,
            e.Subject,
            e.ReceivedAt,
            e.BodyPreview,
            e.ClassificationType?.ToString(),
            e.ClassificationConfidence,
            e.ProcessingStatus.ToString()
        )).ToList();

        return Ok(new ApiResponse<IReadOnlyList<ReviewQueueItemDto>>(
            dtos,
            new PaginationMeta(page, pageSize, totalCount)
        ));
    }

    /// <summary>
    /// Approve a manually reviewed email with optional corrections.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult> ApproveReview(Guid id, [FromBody] ApproveReviewRequest request, CancellationToken ct)
    {
        var email = await _emailRepo.GetPendingAsync(1, ct);
        // In production: find by ID, apply corrections, reprocess
        // For now, mark as approved and trigger reprocessing
        await _orchestrator.ProcessEmailAsync(id, ct);
        return Ok();
    }

    /// <summary>
    /// Reprocess a failed or review-queued email (uses existing classification).
    /// Resets status to Classified so the orchestrator's idempotency guard doesn't skip it.
    /// </summary>
    [HttpPost("{id:guid}/reprocess")]
    public async Task<ActionResult> Reprocess(Guid id, CancellationToken ct)
    {
        var email = await _db.EmailMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.EmailMessageId == id, ct);
        if (email is null)
            return NotFound(new { error = "Email not found" });

        // Reset status so the orchestrator doesn't skip it (idempotency guard checks for Parsed)
        email.ProcessingStatus = ProcessingStatus.Classified;
        email.ErrorDetails = null;
        email.ProcessedAt = null;
        await _db.SaveChangesAsync(ct);

        try
        {
            await _orchestrator.ProcessEmailAsync(id, ct);
            return Ok(new { status = "success", emailId = id });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                status = "error",
                emailId = id,
                error = ex.Message,
                innerError = ex.InnerException?.Message,
                stackTrace = ex.StackTrace?[..Math.Min(ex.StackTrace.Length, 500)]
            });
        }
    }

    /// <summary>
    /// Reclassify and reprocess an email. Re-runs the AI classifier with the latest prompt,
    /// then reprocesses through the parsing pipeline. Use this when classification prompts
    /// have been updated and emails need to be re-evaluated.
    /// </summary>
    [HttpPost("{id:guid}/reclassify")]
    public async Task<ActionResult> Reclassify(Guid id, CancellationToken ct)
    {
        var email = await _db.EmailMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.EmailMessageId == id, ct);
        if (email is null)
            return NotFound(new { error = "Email not found" });

        // Set tenant context
        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
            email.TenantId.ToString());

        // Retrieve full body for classification
        var fullBody = email.BodyPreview ?? "";
        if (!string.IsNullOrEmpty(email.BodyBlobUrl))
        {
            try
            {
                var blobBody = await _blobStorage.GetEmailBodyAsync(email.BodyBlobUrl, ct);
                if (!string.IsNullOrEmpty(blobBody))
                    fullBody = ForwardedEmailHelper.ExtractOriginalBody(blobBody);
            }
            catch { /* fall through to preview */ }
        }

        // Re-run the classifier
        var oldType = email.ClassificationType;
        var result = await _classifier.ClassifyAsync(email.Subject, fullBody, email.FromAddress, ct);
        email.ClassificationType = result.Type;
        email.ClassificationConfidence = result.Confidence;
        email.ProcessingStatus = ProcessingStatus.Classified;
        email.ProcessedAt = null;
        email.ErrorDetails = null;
        await _db.SaveChangesAsync(ct);

        // Reprocess with the new classification
        await _orchestrator.ProcessEmailAsync(id, ct);

        return Ok(new
        {
            emailId = id,
            oldClassification = oldType?.ToString(),
            newClassification = result.Type.ToString(),
            newConfidence = result.Confidence
        });
    }

    /// <summary>
    /// Reclassify and reprocess ALL emails. Re-runs the classifier on every email
    /// then reprocesses through the parsing pipeline.
    /// </summary>
    [HttpPost("reclassify-all")]
    public async Task<ActionResult> ReclassifyAll(CancellationToken ct)
    {
        var emails = await _db.EmailMessages
            .IgnoreQueryFilters()
            .Where(e => e.ClassificationType != null)
            .OrderBy(e => e.ReceivedAt)
            .ToListAsync(ct);

        if (emails.Count == 0)
            return Ok(new { message = "No emails to reclassify", count = 0 });

        var results = new List<object>();
        foreach (var email in emails)
        {
            // Set tenant context
            await _db.Database.ExecuteSqlRawAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
                email.TenantId.ToString());

            var fullBody = email.BodyPreview ?? "";
            if (!string.IsNullOrEmpty(email.BodyBlobUrl))
            {
                try
                {
                    var blobBody = await _blobStorage.GetEmailBodyAsync(email.BodyBlobUrl, ct);
                    if (!string.IsNullOrEmpty(blobBody))
                        fullBody = ForwardedEmailHelper.ExtractOriginalBody(blobBody);
                }
                catch { /* fall through to preview */ }
            }

            var oldType = email.ClassificationType;
            var classResult = await _classifier.ClassifyAsync(email.Subject, fullBody, email.FromAddress, ct);
            email.ClassificationType = classResult.Type;
            email.ClassificationConfidence = classResult.Confidence;
            email.ProcessingStatus = ProcessingStatus.Classified;
            email.ProcessedAt = null;
            email.ErrorDetails = null;
            await _db.SaveChangesAsync(ct);

            try
            {
                await _orchestrator.ProcessEmailAsync(email.EmailMessageId, ct);
                results.Add(new
                {
                    emailId = email.EmailMessageId,
                    subject = email.Subject,
                    oldClassification = oldType?.ToString(),
                    newClassification = classResult.Type.ToString(),
                    status = "success"
                });
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    emailId = email.EmailMessageId,
                    subject = email.Subject,
                    oldClassification = oldType?.ToString(),
                    newClassification = classResult.Type.ToString(),
                    status = "error",
                    error = ex.Message
                });
            }
        }

        return Ok(new { count = results.Count, results });
    }

    /// <summary>
    /// Debug endpoint: show what the parser sees for a given email.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}/debug")]
    public async Task<ActionResult> DebugEmail(Guid id, CancellationToken ct)
    {
        // Set tenant context for RLS
        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
            "215F9D63-05C2-4C4C-8548-1CD950DC430A");

        var email = await _db.EmailMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.EmailMessageId == id, ct);
        if (email is null)
            return NotFound(new { error = "Email not found" });

        // Get full body from blob
        string? fullBody = null;
        int fullBodyLength = 0;
        if (!string.IsNullOrEmpty(email.BodyBlobUrl))
        {
            fullBody = await _blobStorage.GetEmailBodyAsync(email.BodyBlobUrl, ct);
            fullBodyLength = fullBody?.Length ?? 0;
        }

        var body = fullBody ?? email.BodyPreview ?? "";

        // Pre-process body the same way the orchestrator does
        var rawLen = body.Length;
        var cleanedBody = ForwardedEmailHelper.ExtractOriginalBody(body);
        var cleanedLen = cleanedBody.Length;

        // Try the appropriate parser to see what it returns
        object? parserResult = null;
        string? parserError = null;
        if (email.ClassificationType == EmailClassificationType.OrderConfirmation ||
            email.ClassificationType == EmailClassificationType.OrderModification)
        {
            try
            {
                var result = await _orderParser.ParseAsync(email.Subject, cleanedBody, email.FromAddress, null, ct);
                parserResult = new
                {
                    parserType = "Order",
                    confidence = result.Confidence,
                    needsReview = result.NeedsReview,
                    hasData = result.Data != null,
                    hasOrder = result.Data?.Order != null,
                    lineCount = result.Data?.Lines?.Count ?? 0,
                    orderNumber = result.Data?.Order?.ExternalOrderNumber,
                    totalAmount = result.Data?.Order?.TotalAmount,
                    errorMessage = result.ErrorMessage
                };
            }
            catch (Exception ex)
            {
                parserError = ex.Message;
            }
        }
        else if (email.ClassificationType == EmailClassificationType.DeliveryConfirmation ||
                 email.ClassificationType == EmailClassificationType.DeliveryIssue)
        {
            try
            {
                var result = await _deliveryParser.ParseAsync(email.Subject, cleanedBody, email.FromAddress, null, ct);
                parserResult = new
                {
                    parserType = "Delivery",
                    confidence = result.Confidence,
                    needsReview = result.NeedsReview,
                    hasData = result.Data != null,
                    hasDelivery = result.Data?.Delivery != null,
                    orderReference = result.Data?.Delivery?.OrderReference,
                    status = result.Data?.Delivery?.Status,
                    location = result.Data?.Delivery?.DeliveryLocation,
                    errorMessage = result.ErrorMessage
                };
            }
            catch (Exception ex)
            {
                parserError = ex.Message;
            }
        }

        return Ok(new
        {
            emailId = email.EmailMessageId,
            subject = email.Subject,
            from = email.FromAddress,
            receivedAt = email.ReceivedAt,
            classificationType = email.ClassificationType?.ToString(),
            classificationConfidence = email.ClassificationConfidence,
            processingStatus = email.ProcessingStatus.ToString(),
            errorDetails = email.ErrorDetails,
            blobUrl = email.BodyBlobUrl,
            previewLength = email.BodyPreview?.Length ?? 0,
            fullBodyLength,
            fullBodyAvailable = fullBody != null,
            rawBodyLength = rawLen,
            cleanedBodyLength = cleanedLen,
            bodyFirst500 = cleanedBody.Length > 500 ? cleanedBody[..500] : cleanedBody,
            parserResult,
            parserError
        });
    }

    /// <summary>
    /// Debug endpoint: list all emails with their status.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("debug/list")]
    public async Task<ActionResult> DebugList(CancellationToken ct)
    {
        // Set tenant context for RLS
        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
            "215F9D63-05C2-4C4C-8548-1CD950DC430A");

        var emails = await _db.EmailMessages
            .IgnoreQueryFilters()
            .OrderByDescending(e => e.ReceivedAt)
            .Take(50)
            .Select(e => new
            {
                id = e.EmailMessageId,
                subject = e.Subject,
                from = e.FromAddress,
                receivedAt = e.ReceivedAt,
                classification = e.ClassificationType.ToString(),
                confidence = e.ClassificationConfidence,
                status = e.ProcessingStatus.ToString(),
                error = e.ErrorDetails,
                processedAt = e.ProcessedAt,
                hasBlobUrl = !string.IsNullOrEmpty(e.BodyBlobUrl)
            })
            .ToListAsync(ct);

        return Ok(new { count = emails.Count, emails });
    }

    /// <summary>
    /// Debug endpoint: get processing log entries for an email.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("debug/processing-log/{emailId:guid}")]
    public async Task<ActionResult> DebugProcessingLog(Guid emailId, CancellationToken ct)
    {
        var logs = await _db.Database.SqlQueryRaw<ProcessingLogEntry>(
            "SELECT TOP 100 Step, Status, Message, Details, CreatedAt FROM ProcessingLog WHERE EmailMessageId = {0} ORDER BY CreatedAt DESC",
            emailId).ToListAsync(ct);
        return Ok(logs);
    }

    /// <summary>
    /// Debug endpoint: get full cleaned body for an email (for diagnosing splitter behavior).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("debug/email-body/{emailId:guid}")]
    public async Task<ActionResult> DebugEmailBody(Guid emailId, CancellationToken ct)
    {
        var email = await _db.EmailMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.EmailMessageId == emailId, ct);
        if (email is null)
            return NotFound(new { error = "Email not found" });

        string? rawBody = null;
        if (!string.IsNullOrEmpty(email.BodyBlobUrl))
        {
            try { rawBody = await _blobStorage.GetEmailBodyAsync(email.BodyBlobUrl, ct); }
            catch { }
        }

        var body = rawBody ?? email.BodyPreview ?? "";
        var cleanedBody = ForwardedEmailHelper.ExtractOriginalBody(body);

        // Run the splitter regex to see what order numbers are detected
        var orderNumberPattern = new System.Text.RegularExpressions.Regex(@"\b\d{3}-\d{7}-\d{7}\b");
        var orderNumbers = orderNumberPattern.Matches(cleanedBody)
            .Select(m => m.Value).Distinct().ToList();

        return Ok(new
        {
            emailId,
            rawBodyLength = body.Length,
            cleanedBodyLength = cleanedBody.Length,
            cleanedBody,
            detectedOrderNumbers = orderNumbers
        });
    }

    /// <summary>
    /// Debug endpoint: recalculate status for all orders.
    /// Useful after fixing state machine logic to propagate correct statuses.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("debug/recalculate-all")]
    public async Task<ActionResult> RecalculateAllOrders(CancellationToken ct)
    {
        // Set tenant context for RLS
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
                "215F9D63-05C2-4C4C-8548-1CD950DC430A");

            var orders = await _db.Orders
                .IgnoreQueryFilters()
                .Where(o => o.TenantId == Guid.Parse("215F9D63-05C2-4C4C-8548-1CD950DC430A"))
                .Select(o => new { o.OrderId, OldStatus = o.Status.ToString() })
                .ToListAsync(ct);

            var results = new List<object>();
            foreach (var o in orders)
            {
                var newStatus = await _stateMachine.RecalculateStatusAsync(o.OrderId, ct);
                results.Add(new { o.OrderId, oldStatus = o.OldStatus, newStatus = newStatus.ToString() });
            }

            await _db.SaveChangesAsync(ct);

            return Ok(new { count = results.Count, results });
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Debug endpoint: remove duplicate shipments created by reprocessing.
    /// Keeps the earliest shipment per tracking number (or per SourceEmailId if no tracking),
    /// preserving its delivery and shipment lines. Removes duplicates and their orphaned data.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("debug/cleanup-duplicate-shipments")]
    public async Task<ActionResult> CleanupDuplicateShipments(CancellationToken ct)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var tenantId = Guid.Parse("215F9D63-05C2-4C4C-8548-1CD950DC430A");
            await _db.Database.ExecuteSqlRawAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
                tenantId.ToString());

            var allShipments = await _db.Shipments
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId)
                .Include(s => s.Delivery)
                .Include(s => s.Lines)
                .ToListAsync(ct);

            // Group by OrderId + TrackingNumber (or SourceEmailId if no tracking)
            var groups = allShipments
                .GroupBy(s => new { s.OrderId, Key = s.TrackingNumber ?? s.SourceEmailId.ToString() })
                .Where(g => g.Count() > 1)
                .ToList();

            var removed = new List<object>();
            foreach (var group in groups)
            {
                // Keep the one with a delivery, or the one with the most shipment lines, or the earliest
                var ordered = group
                    .OrderByDescending(s => s.Delivery != null ? 1 : 0)
                    .ThenByDescending(s => s.Lines?.Count ?? 0)
                    .ThenBy(s => s.CreatedAt)
                    .ToList();

                var keep = ordered.First();
                var duplicates = ordered.Skip(1).ToList();

                foreach (var dup in duplicates)
                {
                    // Move any shipment lines from dup to keep (if keep doesn't have them)
                    if (dup.Lines != null)
                    {
                        foreach (var line in dup.Lines.ToList())
                        {
                            var alreadyHas = keep.Lines?.Any(l => l.OrderLineId == line.OrderLineId) ?? false;
                            if (!alreadyHas)
                            {
                                line.ShipmentId = keep.ShipmentId;
                            }
                            else
                            {
                                _db.ShipmentLines.Remove(line);
                            }
                        }
                    }

                    // Move delivery if keep doesn't have one
                    if (dup.Delivery != null && keep.Delivery == null)
                    {
                        dup.Delivery.ShipmentId = keep.ShipmentId;
                        keep.Delivery = dup.Delivery;
                        dup.Delivery = null;
                    }
                    else if (dup.Delivery != null)
                    {
                        _db.Deliveries.Remove(dup.Delivery);
                    }

                    removed.Add(new { dup.ShipmentId, dup.OrderId, dup.TrackingNumber, dup.SourceEmailId });
                    _db.Shipments.Remove(dup);
                }
            }

            await _db.SaveChangesAsync(ct);

            return Ok(new { duplicateGroupsFound = groups.Count, shipmentsRemoved = removed.Count, removed });
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Debug endpoint: reset an order by wiping all child entities (shipments, deliveries,
    /// shipment lines, order events) and resetting order lines to Ordered status.
    /// After calling this, reclassify the shipment/delivery emails to rebuild the data.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("debug/reset-order/{orderId:guid}")]
    public async Task<ActionResult> ResetOrder(Guid orderId, CancellationToken ct)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var tenantId = Guid.Parse("215F9D63-05C2-4C4C-8548-1CD950DC430A");
            await _db.Database.ExecuteSqlRawAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
                tenantId.ToString());

            var order = await _db.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Lines)
                .Include(o => o.Shipments).ThenInclude(s => s.Lines)
                .Include(o => o.Shipments).ThenInclude(s => s.Delivery)
                .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);

            if (order is null)
                return NotFound(new { error = "Order not found" });

            var stats = new {
                orderId,
                orderNumber = order.ExternalOrderNumber,
                shipmentsRemoved = order.Shipments.Count,
                deliveriesRemoved = order.Shipments.Count(s => s.Delivery != null),
                shipmentLinesRemoved = order.Shipments.Sum(s => s.Lines?.Count ?? 0),
                linesReset = order.Lines?.Count ?? 0
            };

            // Remove all deliveries
            foreach (var shipment in order.Shipments)
            {
                if (shipment.Delivery != null)
                    _db.Deliveries.Remove(shipment.Delivery);
                if (shipment.Lines != null)
                    _db.ShipmentLines.RemoveRange(shipment.Lines);
            }

            // Remove all shipments
            _db.Shipments.RemoveRange(order.Shipments);

            // Remove timeline events
            var events = await _db.OrderEvents
                .IgnoreQueryFilters()
                .Where(e => e.OrderId == orderId)
                .ToListAsync(ct);
            _db.OrderEvents.RemoveRange(events);

            // Reset order lines to Ordered status (keep the lines themselves)
            if (order.Lines != null)
            {
                foreach (var line in order.Lines)
                {
                    line.Status = Domain.Enums.OrderLineStatus.Ordered;
                    line.UpdatedAt = DateTime.UtcNow;
                }
            }

            // Reset order status
            order.Status = Domain.Enums.OrderStatus.Placed;
            order.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            return Ok(new { message = "Order reset successfully", stats });
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}

/// <summary>
/// Lightweight DTO for reading ProcessingLog rows via raw SQL.
/// </summary>
public class ProcessingLogEntry
{
    public string Step { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; }
}
