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
    private readonly OrderPulseDbContext _db;
    private readonly EmailBlobStorageService _blobStorage;
    private readonly IEmailParser<OrderParserResult> _orderParser;

    public EmailsController(
        IEmailMessageRepository emailRepo,
        IEmailProcessingOrchestrator orchestrator,
        OrderPulseDbContext db,
        EmailBlobStorageService blobStorage,
        IEmailParser<OrderParserResult> orderParser)
    {
        _emailRepo = emailRepo;
        _orchestrator = orchestrator;
        _db = db;
        _blobStorage = blobStorage;
        _orderParser = orderParser;
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
    /// Reprocess a failed or review-queued email.
    /// </summary>
    [HttpPost("{id:guid}/reprocess")]
    public async Task<ActionResult> Reprocess(Guid id, CancellationToken ct)
    {
        await _orchestrator.ProcessEmailAsync(id, ct);
        return Ok();
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

        // Try order parser to see what it returns
        object? parserResult = null;
        string? parserError = null;
        if (email.ClassificationType == EmailClassificationType.OrderConfirmation ||
            email.ClassificationType == EmailClassificationType.OrderModification)
        {
            try
            {
                var result = await _orderParser.ParseAsync(email.Subject, body, email.FromAddress, null, ct);
                parserResult = new
                {
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
            bodyFirst500 = body.Length > 500 ? body[..500] : body,
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
}
