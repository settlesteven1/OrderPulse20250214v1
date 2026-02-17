using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderPulse.Api.DTOs;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IOrderRepository _orderRepo;
    private readonly IReturnRepository _returnRepo;
    private readonly OrderPulseDbContext _db;

    public DashboardController(IOrderRepository orderRepo, IReturnRepository returnRepo, OrderPulseDbContext db)
    {
        _orderRepo = orderRepo;
        _returnRepo = returnRepo;
        _db = db;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<DashboardSummaryDto>>> GetSummary(CancellationToken ct)
    {
        var statusCounts = await _orderRepo.GetStatusCountsAsync(ct);
        var awaitingRefund = await _returnRepo.GetAwaitingRefundAsync(ct);

        var awaitingDelivery = new[] {
            OrderStatus.Placed, OrderStatus.PartiallyShipped, OrderStatus.Shipped,
            OrderStatus.InTransit, OrderStatus.OutForDelivery
        }.Sum(s => statusCounts.GetValueOrDefault(s, 0));

        var needsAttention = statusCounts.GetValueOrDefault(OrderStatus.DeliveryException, 0);

        var openReturns = statusCounts.GetValueOrDefault(OrderStatus.ReturnInProgress, 0);

        // Delivered this week: would need a separate query in production
        // For now, approximate from status counts
        var deliveredThisWeek = statusCounts.GetValueOrDefault(OrderStatus.Delivered, 0);

        var pendingRefundTotal = awaitingRefund.Sum(r =>
            r.Lines.Sum(l => l.OrderLine?.UnitPrice ?? 0 * l.Quantity));

        var dto = new DashboardSummaryDto(
            AwaitingDelivery: awaitingDelivery,
            NeedsAttention: needsAttention,
            OpenReturns: openReturns,
            AwaitingRefund: awaitingRefund.Count,
            DeliveredThisWeek: deliveredThisWeek,
            PendingRefundTotal: pendingRefundTotal
        );

        return Ok(new ApiResponse<DashboardSummaryDto>(dto));
    }

    [HttpGet("activity")]
    public async Task<ActionResult<ApiResponse<List<TimelineEventDto>>>> GetRecentActivity(
        [FromQuery] int count = 20, CancellationToken ct = default)
    {
        var events = await _db.OrderEvents
            .OrderByDescending(e => e.EventDate)
            .Take(count)
            .Select(e => new TimelineEventDto(
                e.EventId,
                e.EventType,
                e.EventDate,
                e.Summary ?? "",
                e.EntityType,
                e.EntityId
            ))
            .ToListAsync(ct);

        return Ok(new ApiResponse<List<TimelineEventDto>>(events));
    }
}
