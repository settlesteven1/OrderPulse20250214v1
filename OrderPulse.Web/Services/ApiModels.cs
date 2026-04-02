namespace OrderPulse.Web.Services;

/// <summary>
/// Client-side DTOs matching the API response shapes.
/// </summary>

public class ApiResponse<T>
{
    public T? Data { get; set; }
    public PaginationMeta? Meta { get; set; }
}

public class PaginationMeta
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}

// ── Dashboard ──

public class DashboardSummary
{
    public int AwaitingDelivery { get; set; }
    public int NeedsAttention { get; set; }
    public int OpenReturns { get; set; }
    public int AwaitingRefund { get; set; }
    public int DeliveredThisWeek { get; set; }
    public decimal PendingRefundTotal { get; set; }
}

// ── Orders ──

public class OrderListItem
{
    public Guid OrderId { get; set; }
    public string ExternalOrderNumber { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal? TotalAmount { get; set; }
    public string? Currency { get; set; } = "USD";
    public int ItemCount { get; set; }
    public string? ItemsPreview { get; set; }
    public RetailerSummary? Retailer { get; set; }
    public string? LastEvent { get; set; }
    public DateTime? LastEventDate { get; set; }
    public string? RetailerName => Retailer?.Name;
    public string? FirstItemName => ItemsPreview;
}

public class RetailerSummary
{
    public Guid RetailerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public int? ReturnPolicyDays { get; set; }
}

public class OrderDetailModel
{
    public Guid OrderId { get; set; }
    public string ExternalOrderNumber { get; set; } = string.Empty;
    public string? RetailerName { get; set; }
    public DateTime OrderDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal? Subtotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? ShippingCost { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? ShippingAddress { get; set; }
    public string? PaymentMethodSummary { get; set; }
    public DateOnly? EstimatedDeliveryStart { get; set; }
    public DateOnly? EstimatedDeliveryEnd { get; set; }
    public List<OrderLineDto> Lines { get; set; } = new();
    public List<ShipmentDto> Shipments { get; set; } = new();
    public List<ReturnDto> Returns { get; set; } = new();
    public List<RefundDto> Refunds { get; set; } = new();
}

public class OrderLineDto
{
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
}

public class ShipmentDto
{
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateOnly? EstimatedDelivery { get; set; }
}

public class ReturnDto
{
    public Guid ReturnId { get; set; }
    public string? RMANumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ReturnReason { get; set; }
    public DateOnly? ReturnByDate { get; set; }
}

public class RefundDto
{
    public decimal RefundAmount { get; set; }
    public string? RefundMethod { get; set; }
    public DateTime? RefundDate { get; set; }
}

// ── Timeline ──

public class TimelineEvent
{
    public string EventType { get; set; } = string.Empty;
    public DateTime EventDate { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }
}

// ── Inventory ──

public class InventoryItemModel
{
    public Guid InventoryItemId { get; set; }
    public Guid OrderLineId { get; set; }
    public Guid OrderId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string ItemCategory { get; set; } = string.Empty;
    public int QuantityOnHand { get; set; }
    public string? UnitStatus { get; set; }
    public string? Condition { get; set; }
    public DateTime? PurchaseDate { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string? ExternalOrderNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class InventoryItemDetailModel : InventoryItemModel
{
    public List<InventoryAdjustmentModel> RecentAdjustments { get; set; } = new();
}

public class RelatedOrderModel
{
    public Guid OrderId { get; set; }
    public string ExternalOrderNumber { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal? TotalAmount { get; set; }
    public string? Currency { get; set; }
    public string? RetailerName { get; set; }
    public List<RelatedOrderLineModel> MatchingLines { get; set; } = new();
}

public class RelatedOrderLineModel
{
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
}

public class InventoryAdjustmentModel
{
    public Guid AdjustmentId { get; set; }
    public int QuantityDelta { get; set; }
    public int PreviousQuantity { get; set; }
    public int NewQuantity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? AdjustedBy { get; set; }
    public DateTime AdjustedAt { get; set; }
}
