using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderPulse.Api.DTOs;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;

namespace OrderPulse.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly IOrderRepository _orderRepo;

    public OrdersController(IOrderRepository orderRepo)
    {
        _orderRepo = orderRepo;
    }

    /// <summary>
    /// List orders with filtering, search, and pagination.
    /// Supports smart filter shortcuts: awaiting-delivery, needs-attention,
    /// open-returns, awaiting-refund, recently-delivered, all-closed.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<OrderListItemDto>>>> GetOrders(
        [FromQuery] string? status,
        [FromQuery] string? shortcut,
        [FromQuery] Guid? retailer,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string sort = "OrderDate",
        [FromQuery] bool desc = true,
        CancellationToken ct = default)
    {
        var query = new OrderQueryParameters
        {
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, 100),
            Status = Enum.TryParse<OrderStatus>(status, true, out var s) ? s : null,
            StatusShortcut = shortcut,
            RetailerId = retailer,
            DateFrom = dateFrom,
            DateTo = dateTo,
            Search = search,
            SortBy = sort,
            SortDescending = desc
        };

        var (items, totalCount) = await _orderRepo.GetOrdersAsync(query, ct);

        var dtos = items.Select(o => new OrderListItemDto(
            o.OrderId,
            o.ExternalOrderNumber,
            o.OrderDate,
            o.Status.ToString(),
            o.TotalAmount,
            o.Currency,
            o.Lines.Sum(l => l.Quantity),
            string.Join(", ", o.Lines.Take(3).Select(l => l.ProductName)),
            o.Retailer is not null ? new RetailerSummaryDto(o.Retailer.RetailerId, o.Retailer.Name, o.Retailer.LogoUrl, o.Retailer.ReturnPolicyDays) : null,
            o.Events.OrderByDescending(e => e.EventDate).FirstOrDefault()?.Summary,
            o.Events.OrderByDescending(e => e.EventDate).FirstOrDefault()?.EventDate
        )).ToList();

        return Ok(new ApiResponse<IReadOnlyList<OrderListItemDto>>(
            dtos,
            new PaginationMeta(query.Page, query.PageSize, totalCount)
        ));
    }

    /// <summary>
    /// Get full order detail including lines, shipments, deliveries, returns, and refunds.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<OrderDetailDto>>> GetOrder(Guid id, CancellationToken ct)
    {
        var order = await _orderRepo.GetByIdAsync(id, ct);
        if (order is null)
            return NotFound(new ApiError("ORDER_NOT_FOUND", $"Order with ID {id} not found"));

        var dto = MapToDetailDto(order);
        return Ok(new ApiResponse<OrderDetailDto>(dto));
    }

    /// <summary>
    /// Get the chronological event timeline for an order.
    /// </summary>
    [HttpGet("{id:guid}/timeline")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TimelineEventDto>>>> GetTimeline(Guid id, CancellationToken ct)
    {
        var order = await _orderRepo.GetByIdAsync(id, ct);
        if (order is null)
            return NotFound(new ApiError("ORDER_NOT_FOUND", $"Order with ID {id} not found"));

        var events = order.Events
            .OrderByDescending(e => e.EventDate)
            .Select(e => new TimelineEventDto(e.EventId, e.EventType, e.EventDate, e.Summary, e.EntityType, e.EntityId))
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<TimelineEventDto>>(events));
    }

    private static OrderDetailDto MapToDetailDto(Domain.Entities.Order o) => new(
        o.OrderId,
        o.ExternalOrderNumber,
        o.ExternalOrderUrl,
        o.OrderDate,
        o.Status.ToString(),
        o.Subtotal,
        o.TaxAmount,
        o.ShippingCost,
        o.DiscountAmount,
        o.TotalAmount,
        o.Currency,
        o.ShippingAddress,
        o.PaymentMethodSummary,
        o.EstimatedDeliveryStart,
        o.EstimatedDeliveryEnd,
        o.IsInferred,
        o.Retailer is not null ? new RetailerSummaryDto(o.Retailer.RetailerId, o.Retailer.Name, o.Retailer.LogoUrl, o.Retailer.ReturnPolicyDays) : null,
        o.Lines.OrderBy(l => l.LineNumber).Select(l => new OrderLineDto(
            l.OrderLineId, l.LineNumber, l.ProductName, l.ProductUrl, l.SKU,
            l.Quantity, l.UnitPrice, l.LineTotal, l.Status.ToString(), l.ImageUrl
        )).ToList(),
        o.Shipments.Select(s => new ShipmentDto(
            s.ShipmentId, s.Carrier, s.TrackingNumber, s.TrackingUrl,
            s.ShipDate, s.EstimatedDelivery, s.Status.ToString(), s.LastStatusUpdate,
            s.Delivery is not null ? new DeliveryDto(
                s.Delivery.DeliveryId, s.Delivery.DeliveryDate, s.Delivery.DeliveryLocation,
                s.Delivery.Status.ToString(), s.Delivery.IssueType?.ToString(), s.Delivery.IssueDescription, s.Delivery.PhotoBlobUrl
            ) : null,
            (s.Lines ?? new List<Domain.Entities.ShipmentLine>()).Select(sl => new ShipmentLineDto(
                sl.OrderLineId, sl.OrderLine?.ProductName ?? "", sl.Quantity
            )).ToList()
        )).ToList(),
        o.Returns.Select(r => new ReturnSummaryDto(
            r.ReturnId, r.RMANumber, r.Status.ToString(), r.ReturnReason, r.ReturnMethod?.ToString(),
            r.ReturnCarrier, r.ReturnTrackingNumber, r.ReturnByDate,
            r.ReturnByDate.HasValue ? (r.ReturnByDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.UtcNow).Days : 0,
            r.Lines.Select(rl => new ReturnLineDto(rl.OrderLineId, rl.OrderLine?.ProductName ?? "", rl.Quantity, rl.ReturnReason)).ToList()
        )).ToList(),
        o.Refunds.Select(r => new RefundDto(
            r.RefundId, r.RefundAmount, r.Currency, r.RefundMethod, r.RefundDate, r.EstimatedArrival, r.TransactionId
        )).ToList()
    );
}
