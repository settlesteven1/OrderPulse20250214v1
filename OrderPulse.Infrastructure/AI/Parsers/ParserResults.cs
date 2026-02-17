namespace OrderPulse.Infrastructure.AI.Parsers;

/// <summary>
/// Result DTOs matching the JSON schemas defined in the AI prompt templates.
/// These are deserialized directly from the AI responses using snake_case naming.
/// </summary>

// ── Order Parser ──

public class OrderParserResult
{
    /// <summary>Single order (legacy / simple emails)</summary>
    public OrderData? Order { get; set; }
    /// <summary>Line items for the single order</summary>
    public List<OrderLineData> Lines { get; set; } = new();

    /// <summary>
    /// Multiple orders extracted from a single email (e.g. Amazon splitting one purchase
    /// across multiple fulfillers). Each entry has its own order data and line items.
    /// When this is populated, Order/Lines above may be null.
    /// </summary>
    public List<OrderWithLines>? Orders { get; set; }

    public double Confidence { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Returns all orders in a normalized list, whether they came from the single Order
    /// property or from the Orders array.
    /// </summary>
    public List<OrderWithLines> GetAllOrders()
    {
        var result = new List<OrderWithLines>();
        if (Orders is not null && Orders.Count > 0)
        {
            result.AddRange(Orders);
        }
        else if (Order is not null)
        {
            result.Add(new OrderWithLines { Order = Order, Lines = Lines });
        }
        return result;
    }
}

/// <summary>
/// Groups an order with its line items, used for multi-order emails.
/// </summary>
public class OrderWithLines
{
    public OrderData Order { get; set; } = new();
    public List<OrderLineData> Lines { get; set; } = new();
}

public class OrderData
{
    public string ExternalOrderNumber { get; set; } = string.Empty;
    public string? RetailerName { get; set; }
    public string? OrderDate { get; set; }
    public decimal? Subtotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? ShippingCost { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? EstimatedDeliveryStart { get; set; }
    public string? EstimatedDeliveryEnd { get; set; }
    public string? ShippingAddress { get; set; }
    public string? PaymentMethodSummary { get; set; }
    public string? ExternalOrderUrl { get; set; }
    public bool IsModification { get; set; }
}

public class OrderLineData
{
    public string ProductName { get; set; } = string.Empty;
    public string? ProductUrl { get; set; }
    public string? Sku { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
    public string? ImageUrl { get; set; }
}

// ── Shipment Parser ──

public class ShipmentParserResult
{
    public List<ShipmentData> Shipments { get; set; } = new();
    public double Confidence { get; set; }
    public string? Notes { get; set; }
}

public class ShipmentData
{
    public string? OrderReference { get; set; }
    public string? Carrier { get; set; }
    public string? CarrierNormalized { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
    public string? ShipDate { get; set; }
    public string? EstimatedDelivery { get; set; }
    public string? Status { get; set; }
    public string? StatusDetail { get; set; }
    public List<ShipmentItemData> Items { get; set; } = new();
}

public class ShipmentItemData
{
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}

// ── Delivery Parser ──

public class DeliveryParserResult
{
    public DeliveryData? Delivery { get; set; }
    public double Confidence { get; set; }
    public string? Notes { get; set; }
}

public class DeliveryData
{
    public string? OrderReference { get; set; }
    public string? TrackingNumber { get; set; }
    public string? DeliveryDate { get; set; }
    public string? DeliveryLocation { get; set; }
    public string? Status { get; set; }
    public string? IssueType { get; set; }
    public string? IssueDescription { get; set; }
    public string? SignedBy { get; set; }
    public string? PhotoUrl { get; set; }
}

// ── Return Parser ──

public class ReturnParserResult
{
    public ReturnData? Return { get; set; }
    public List<ReturnItemData> Items { get; set; } = new();
    public double Confidence { get; set; }
    public string? Notes { get; set; }
}

public class ReturnData
{
    public string? OrderReference { get; set; }
    public string? RmaNumber { get; set; }
    public string? Subtype { get; set; }
    public string? Status { get; set; }
    public string? ReturnReason { get; set; }
    public string? ReturnMethod { get; set; }
    public string? ReturnCarrier { get; set; }
    public string? ReturnTrackingNumber { get; set; }
    public string? ReturnTrackingUrl { get; set; }
    public bool HasPrintableLabel { get; set; }
    public bool QrCodeInEmail { get; set; }
    public string? DropOffLocation { get; set; }
    public string? DropOffAddress { get; set; }
    public string? ReturnByDate { get; set; }
    public string? ReceivedByRetailerDate { get; set; }
    public string? RejectionReason { get; set; }
    public decimal? EstimatedRefundAmount { get; set; }
    public string? EstimatedRefundTimeline { get; set; }
}

public class ReturnItemData
{
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public string? ReturnReason { get; set; }
}

// ── Refund Parser ──

public class RefundParserResult
{
    public RefundData? Refund { get; set; }
    public double Confidence { get; set; }
    public string? Notes { get; set; }
}

public class RefundData
{
    public string? OrderReference { get; set; }
    public string? ReturnRma { get; set; }
    public decimal RefundAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? RefundMethod { get; set; }
    public string? RefundDate { get; set; }
    public string? EstimatedArrival { get; set; }
    public string? TransactionId { get; set; }
    public bool IsPartial { get; set; }
    public string? PartialReason { get; set; }
}

// ── Cancellation Parser ──

public class CancellationParserResult
{
    public CancellationData? Cancellation { get; set; }
    public List<CancelledItemData> CancelledItems { get; set; } = new();
    public List<RemainingItemData> RemainingItems { get; set; } = new();
    public double Confidence { get; set; }
    public string? Notes { get; set; }
}

public class CancellationData
{
    public string? OrderReference { get; set; }
    public bool IsFullCancellation { get; set; }
    public string? CancellationReason { get; set; }
    public string? InitiatedBy { get; set; }
    public decimal? RefundAmount { get; set; }
    public string? RefundMethod { get; set; }
    public string? RefundTimeline { get; set; }
}

public class CancelledItemData
{
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
}

public class RemainingItemData
{
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}

// ── Payment Parser ──

public class PaymentParserResult
{
    public PaymentData? Payment { get; set; }
    public double Confidence { get; set; }
    public string? Notes { get; set; }
}

public class PaymentData
{
    public string? OrderReference { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? PaymentMethod { get; set; }
    public string? TransactionId { get; set; }
    public string? PaymentDate { get; set; }
    public string? RetailerName { get; set; }
}
