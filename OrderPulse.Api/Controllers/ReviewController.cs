using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPulse.Api.DTOs;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.Services;

namespace OrderPulse.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReviewController : ControllerBase
{
    private readonly IEmailMessageRepository _emailRepo;
    private readonly IEmailProcessingOrchestrator _orchestrator;
    private readonly EmailBlobStorageService? _blobService;

    public ReviewController(
        IEmailMessageRepository emailRepo,
        IEmailProcessingOrchestrator orchestrator,
        EmailBlobStorageService? blobService = null)
    {
        _emailRepo = emailRepo;
        _orchestrator = orchestrator;
        _blobService = blobService;
    }

    /// <summary>
    /// Get paginated review queue (ManualReview + Failed emails).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ReviewQueueItemDto>>>> GetQueue(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
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
    /// Get full detail for a review item including email body.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ReviewDetailDto>>> GetDetail(Guid id, CancellationToken ct)
    {
        // Use IgnoreQueryFilters via a direct lookup since GetReviewQueueAsync is paginated
        var (items, _) = await _emailRepo.GetReviewQueueAsync(1, 1000, ct);
        var email = items.FirstOrDefault(e => e.EmailMessageId == id);

        if (email is null)
            return NotFound(new ApiError("EMAIL_NOT_FOUND", $"Email {id} not found in review queue."));

        // Try to fetch the full body from blob storage
        string? bodyHtml = null;
        if (_blobService is not null && !string.IsNullOrEmpty(email.BodyBlobUrl))
        {
            try { bodyHtml = await _blobService.GetEmailBodyAsync(email.BodyBlobUrl, ct); }
            catch { /* fall through to preview */ }
        }

        var dto = new ReviewDetailDto(
            email.EmailMessageId,
            email.FromAddress,
            email.FromDisplayName,
            email.Subject,
            email.ReceivedAt,
            email.BodyPreview,
            bodyHtml,
            email.ClassificationType?.ToString(),
            email.ClassificationConfidence,
            email.ProcessingStatus.ToString(),
            email.ErrorDetails
        );

        return Ok(new ApiResponse<ReviewDetailDto>(dto));
    }

    /// <summary>
    /// Approve with optional corrections, then reprocess.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult> Approve(Guid id, [FromBody] ApproveReviewRequest request, CancellationToken ct)
    {
        // TODO: Apply corrections to the EmailMessage before reprocessing
        await _orchestrator.ProcessEmailAsync(id, ct);
        return Ok();
    }

    /// <summary>
    /// Dismiss an email from the review queue (mark as Dismissed).
    /// </summary>
    [HttpPost("{id:guid}/dismiss")]
    public async Task<ActionResult> Dismiss(Guid id, CancellationToken ct)
    {
        var (items, _) = await _emailRepo.GetReviewQueueAsync(1, 1000, ct);
        var email = items.FirstOrDefault(e => e.EmailMessageId == id);
        if (email is null)
            return NotFound();

        email.ProcessingStatus = ProcessingStatus.Dismissed;
        email.ProcessedAt = DateTime.UtcNow;
        await _emailRepo.UpdateAsync(email, ct);
        return Ok();
    }

    /// <summary>
    /// Re-run the AI pipeline on a failed or reviewed email.
    /// </summary>
    [HttpPost("{id:guid}/reprocess")]
    public async Task<ActionResult> Reprocess(Guid id, CancellationToken ct)
    {
        await _orchestrator.ProcessEmailAsync(id, ct);
        return Ok();
    }
}
