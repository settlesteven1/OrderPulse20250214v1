using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;
using OrderPulse.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Computes the aggregate order status based on the state of all child entities.
/// Called after any email is processed that affects an order.
/// </summary>
public class OrderStateMachine
{
    private readonly OrderPulseDbContext _db;

    public OrderStateMachine(OrderPulseDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Recalculates the order status based on current child entity states.
    /// Returns the new status (and updates the order entity).
    /// </summary>
    public async Task<OrderStatus> RecalculateStatusAsync(Guid orderId, CancellationToken ct = default)
    {
        // Use IgnoreQueryFilters to bypass RLS — caller already verified tenant ownership
        var order = await _db.Orders
            .IgnoreQueryFilters()
            .Include(o => o.Lines)
            .Include(o => o.Shipments).ThenInclude(s => s.Delivery)
            .Include(o => o.Returns)
            .Include(o => o.Refunds)
            .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);

        if (order is null) return OrderStatus.Placed; // Gracefully handle missing order

        var newStatus = ComputeStatus(order);
        if (order.Status != newStatus)
        {
            order.Status = newStatus;
            order.UpdatedAt = DateTime.UtcNow;
        }

        return newStatus;
    }

    private static OrderStatus ComputeStatus(Order order)
    {
        var totalLines = order.Lines.Sum(l => l.Quantity);

        // For stub orders with no lines, compute status directly from child entities.
        // This handles the case where shipment/delivery emails arrive before the order confirmation.
        if (totalLines == 0)
            return ComputeStatusFromChildEntities(order);

        // Check cancellations
        var cancelledQty = order.Lines.Where(l => l.Status == OrderLineStatus.Cancelled).Sum(l => l.Quantity);
        if (cancelledQty == totalLines)
            return OrderStatus.Cancelled;

        // Check if there are active returns
        var hasOpenReturns = order.Returns.Any(r =>
            r.Status is ReturnStatus.Initiated or ReturnStatus.LabelIssued or ReturnStatus.Shipped);
        var hasReceivedReturns = order.Returns.Any(r => r.Status == ReturnStatus.Received);

        // Check refunds
        var allRefunded = order.Lines.All(l =>
            l.Status is OrderLineStatus.Refunded or OrderLineStatus.Cancelled);
        if (allRefunded && order.Lines.Any(l => l.Status == OrderLineStatus.Refunded))
            return OrderStatus.Refunded;

        // Check deliveries
        var deliveredShipments = order.Shipments
            .Where(s => s.Delivery?.Status == DeliveryStatus.Delivered)
            .ToList();
        var exceptionShipments = order.Shipments
            .Where(s => s.Delivery?.Status is DeliveryStatus.DeliveryException or DeliveryStatus.Lost)
            .ToList();

        if (exceptionShipments.Any())
            return OrderStatus.DeliveryException;

        // Return states take priority over delivery states
        if (hasReceivedReturns)
            return OrderStatus.ReturnReceived;
        if (hasOpenReturns)
            return OrderStatus.ReturnInProgress;

        // Delivery progress
        var shippedLines = order.Shipments.SelectMany(s => s.Lines ?? Enumerable.Empty<ShipmentLine>()).Sum(sl => sl.Quantity);
        var deliveredLines = deliveredShipments.SelectMany(s => s.Lines ?? Enumerable.Empty<ShipmentLine>()).Sum(sl => sl.Quantity);
        var activeLines = totalLines - cancelledQty;

        if (deliveredLines >= activeLines)
        {
            // Check if order should be Closed (all delivered, no open returns, 30+ days)
            var lastDelivery = deliveredShipments
                .Select(s => s.Delivery?.DeliveryDate)
                .Where(d => d.HasValue)
                .Max();
            if (lastDelivery.HasValue && DateTime.UtcNow - lastDelivery.Value > TimeSpan.FromDays(30)
                && !hasOpenReturns && !hasReceivedReturns)
                return OrderStatus.Closed;

            return OrderStatus.Delivered;
        }

        if (deliveredLines > 0)
            return OrderStatus.PartiallyDelivered;

        // Shipment progress
        var hasOutForDelivery = order.Shipments.Any(s => s.Status == ShipmentStatus.OutForDelivery);
        if (hasOutForDelivery)
            return OrderStatus.OutForDelivery;

        var hasInTransit = order.Shipments.Any(s => s.Status == ShipmentStatus.InTransit);
        if (hasInTransit)
            return OrderStatus.InTransit;

        if (shippedLines >= activeLines)
            return OrderStatus.Shipped;

        if (shippedLines > 0)
            return OrderStatus.PartiallyShipped;

        if (cancelledQty > 0)
            return OrderStatus.PartiallyCancelled;

        return OrderStatus.Placed;
    }

    /// <summary>
    /// Computes status for stub orders that have no line items yet.
    /// Uses shipment/delivery/return entities directly instead of line-level tracking.
    /// This ensures stub orders display the correct status in the UI while waiting
    /// for the order confirmation email to arrive with line details.
    /// </summary>
    private static OrderStatus ComputeStatusFromChildEntities(Order order)
    {
        // Check returns
        var hasOpenReturns = order.Returns.Any(r =>
            r.Status is ReturnStatus.Initiated or ReturnStatus.LabelIssued or ReturnStatus.Shipped);
        var hasReceivedReturns = order.Returns.Any(r => r.Status == ReturnStatus.Received);

        if (hasReceivedReturns) return OrderStatus.ReturnReceived;
        if (hasOpenReturns) return OrderStatus.ReturnInProgress;

        // Check refunds
        if (order.Refunds.Any()) return OrderStatus.Refunded;

        // Check deliveries
        var hasDelivered = order.Shipments.Any(s =>
            s.Delivery?.Status == DeliveryStatus.Delivered);
        var hasDeliveryException = order.Shipments.Any(s =>
            s.Delivery?.Status is DeliveryStatus.DeliveryException or DeliveryStatus.Lost);

        if (hasDeliveryException) return OrderStatus.DeliveryException;
        if (hasDelivered) return OrderStatus.Delivered;

        // Check shipment progress
        var hasOutForDelivery = order.Shipments.Any(s => s.Status == ShipmentStatus.OutForDelivery);
        if (hasOutForDelivery) return OrderStatus.OutForDelivery;

        var hasDeliveredShipment = order.Shipments.Any(s => s.Status == ShipmentStatus.Delivered);
        if (hasDeliveredShipment) return OrderStatus.Delivered;

        var hasInTransit = order.Shipments.Any(s => s.Status == ShipmentStatus.InTransit);
        if (hasInTransit) return OrderStatus.InTransit;

        if (order.Shipments.Any()) return OrderStatus.Shipped;

        return OrderStatus.Placed;
    }
}
