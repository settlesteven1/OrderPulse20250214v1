namespace OrderPulse.Web.Services;

/// <summary>
/// Client-side DTOs matching the API response shapes.
/// </summary>

public class ApiResponse<T>
{
    public T? Data { get; set; }
    public PaginationMeta? Pagination { get; set; }
}

public class PaginationMeta
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
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
    public string? RetailerName { get; set; }
    public DateTime OrderDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal? TotalAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public int ItemCount { get; set; }
    public string? FirstItemName { get; set; }
    public DateOnly? EstimatedDeliveryEnd { get; set; }
}

public class OrderDetail
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
