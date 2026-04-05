using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Api.DTOs;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.Data;
using OrderPulse.Infrastructure.Services;

namespace OrderPulse.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReturnsController : ControllerBase
{
    private readonly IReturnRepository _returnRepo;
    private readonly OrderPulseDbContext _db;
    private readonly EmailBlobStorageService _blobStorage;

    public ReturnsController(
        IReturnRepository returnRepo,
        OrderPulseDbContext db,
        EmailBlobStorageService blobStorage)
    {
        _returnRepo = returnRepo;
        _db = db;
        _blobStorage = blobStorage;
    }

    /// <summary>
    /// List all returns with optional filtering by status and order.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ReturnDetailDto>>>> GetReturns(
        [FromQuery] string? status,
        [FromQuery] Guid? orderId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var query = new ReturnQueryParameters
        {
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, 100),
            Status = Enum.TryParse<ReturnStatus>(status, true, out var s) ? s : null,
            OrderId = orderId
        };

        var (items, totalCount) = await _returnRepo.GetReturnsAsync(query, ct);
        var dtos = items.Select(MapToDetailDto).ToList();

        return Ok(new ApiResponse<IReadOnlyList<ReturnDetailDto>>(
            dtos,
            new PaginationMeta(query.Page, query.PageSize, totalCount)
        ));
    }

    /// <summary>
    /// Get all open returns with labels/QR codes for the Return Center view.
    /// </summary>
    [HttpGet("labels")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ReturnDetailDto>>>> GetReturnLabels(CancellationToken ct)
    {
        var items = await _returnRepo.GetOpenReturnLabelsAsync(ct);
        var dtos = items
            .OrderBy(r => r.ReturnByDate) // Closest deadline first
            .Select(MapToDetailDto)
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<ReturnDetailDto>>(dtos));
    }

    /// <summary>
    /// Get returns that have been received by the retailer but have no refund yet.
    /// </summary>
    [HttpGet("awaiting-refund")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ReturnDetailDto>>>> GetAwaitingRefund(CancellationToken ct)
    {
        var items = await _returnRepo.GetAwaitingRefundAsync(ct);
        var dtos = items.Select(MapToDetailDto).ToList();
        return Ok(new ApiResponse<IReadOnlyList<ReturnDetailDto>>(dtos));
    }

    /// <summary>
    /// Re-extract QR code image from the source email's raw HTML for an existing return.
    /// Used to backfill returns that were processed before QR extraction was implemented.
    /// </summary>
    [HttpPost("{id:guid}/refresh-qr")]
    public async Task<ActionResult<ApiResponse<ReturnDetailDto>>> RefreshQrCode(
        Guid id, CancellationToken ct)
    {
        var returnEntity = await _db.Returns
            .Include(r => r.Order).ThenInclude(o => o!.Retailer)
            .Include(r => r.Lines).ThenInclude(l => l.OrderLine)
            .Include(r => r.Refund)
            .Include(r => r.SourceEmail)
            .FirstOrDefaultAsync(r => r.ReturnId == id, ct);

        if (returnEntity is null)
            return NotFound(new ApiError("NOT_FOUND", "Return not found"));

        if (returnEntity.SourceEmail?.BodyBlobUrl is null)
            return BadRequest(new ApiError("NO_BODY", "Source email has no stored body"));

        var rawHtml = await _blobStorage.GetEmailBodyAsync(returnEntity.SourceEmail.BodyBlobUrl, ct);
        if (string.IsNullOrEmpty(rawHtml))
            return BadRequest(new ApiError("EMPTY_BODY", "Email body is empty"));

        var qrData = ForwardedEmailHelper.ExtractQrCodeImageData(rawHtml);
        if (qrData is null)
            return Ok(new ApiResponse<ReturnDetailDto>(MapToDetailDto(returnEntity))
            {
                Meta = new PaginationMeta(0, 0, 0) // signal no QR found but not an error
            });

        returnEntity.QRCodeData = qrData;
        returnEntity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new ApiResponse<ReturnDetailDto>(MapToDetailDto(returnEntity)));
    }

    private static ReturnDetailDto MapToDetailDto(Domain.Entities.Return r) => new(
        r.ReturnId,
        r.OrderId,
        r.Order?.ExternalOrderNumber ?? "",
        r.RMANumber,
        r.Status.ToString(),
        r.ReturnReason,
        r.ReturnMethod?.ToString(),
        r.ReturnCarrier,
        r.ReturnTrackingNumber,
        r.ReturnTrackingUrl,
        r.ReturnLabelBlobUrl,
        r.QRCodeBlobUrl,
        r.QRCodeData,
        r.DropOffLocation,
        r.DropOffAddress,
        r.ReturnByDate,
        r.ReturnByDate.HasValue ? (r.ReturnByDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.UtcNow).Days : 0,
        r.ReceivedByRetailerDate,
        r.RejectionReason,
        r.Order?.Retailer is not null
            ? new RetailerSummaryDto(r.Order.Retailer.RetailerId, r.Order.Retailer.Name, r.Order.Retailer.LogoUrl, r.Order.Retailer.ReturnPolicyDays)
            : null,
        r.Lines.Select(rl => new ReturnLineDto(rl.OrderLineId, rl.OrderLine?.ProductName ?? "", rl.Quantity, rl.ReturnReason)).ToList(),
        r.Refund is not null ? new RefundDto(
            r.Refund.RefundId, r.Refund.RefundAmount, r.Refund.Currency,
            r.Refund.RefundMethod, r.Refund.RefundDate, r.Refund.EstimatedArrival, r.Refund.TransactionId
        ) : null
    );
}
