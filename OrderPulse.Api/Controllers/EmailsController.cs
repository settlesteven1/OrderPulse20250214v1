using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPulse.Api.DTOs;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EmailsController : ControllerBase
{
    private readonly IEmailMessageRepository _emailRepo;
    private readonly IEmailProcessingOrchestrator _orchestrator;

    public EmailsController(IEmailMessageRepository emailRepo, IEmailProcessingOrchestrator orchestrator)
    {
        _emailRepo = emailRepo;
        _orchestrator = orchestrator;
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
}
