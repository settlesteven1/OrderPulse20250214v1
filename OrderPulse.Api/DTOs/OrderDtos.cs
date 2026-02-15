namespace OrderPulse.Api.DTOs;

// ── Response Envelope ──
public record ApiResponse<T>(T Data, PaginationMeta? Meta = null);
public record PaginationMeta(int Page, int PageSize, int TotalCount);
public record ApiError(string Code, string Message, string? TraceId = null);

// ── Dashboard ──
public record DashboardSummaryDto(
    int AwaitingDelivery,
    int NeedsAttention,
    int OpenReturns,
    int AwaitingRefund,
    int DeliveredThisWeek,
    decimal PendingRefundTotal
);

// ── Orders ──
public record OrderListItemDto(
    Guid OrderId,
    string ExternalOrderNumber,
    DateTime OrderDate,
    string Status,
    decimal? TotalAmount,
    string? Currency,
    int ItemCount,
    string? ItemsPreview,
    RetailerSummaryDto? Retailer,
    string? LastEvent,
    DateTime? LastEventDate
);

public record OrderDetailDto(
    Guid OrderId,
    string ExternalOrderNumber,
    string? ExternalOrderUrl,
    DateTime OrderDate,
    string Status,
    decimal? Subtotal,
    decimal? TaxAmount,
    decimal? ShippingCost,
    decimal? DiscountAmount,
    decimal? TotalAmount,
    string? Currency,
    string? ShippingAddress,
    string? PaymentMethodSummary,
    DateOnly? EstimatedDeliveryStart,
    DateOnly? EstimatedDeliveryEnd,
    bool IsInferred,
    RetailerSummaryDto? Retailer,
    IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<ShipmentDto> Shipments,
    IReadOnlyList<ReturnSummaryDto> Returns,
    IReadOnlyList<RefundDto> Refunds
);

public record OrderLineDto(
    Guid OrderLineId,
    int LineNumber,
    string ProductName,
    string? ProductUrl,
    string? SKU,
    int Quantity,
    decimal? UnitPrice,
    decimal? LineTotal,
    string Status,
    string? ImageUrl
);

// ── Retailers ──
public record RetailerSummaryDto(
    Guid RetailerId,
    string Name,
    string? LogoUrl,
    int? ReturnPolicyDays
);

// ── Shipments ──
public record ShipmentDto(
    Guid ShipmentId,
    string? Carrier,
    string? TrackingNumber,
    string? TrackingUrl,
    DateTime? ShipDate,
    DateOnly? EstimatedDelivery,
    string Status,
    string? LastStatusUpdate,
    DeliveryDto? Delivery,
    IReadOnlyList<ShipmentLineDto> Lines
);

public record ShipmentLineDto(
    Guid OrderLineId,
    string ProductName,
    int Quantity
);

public record DeliveryDto(
    Guid DeliveryId,
    DateTime? DeliveryDate,
    string? DeliveryLocation,
    string Status,
    string? IssueType,
    string? IssueDescription,
    string? PhotoBlobUrl
);

// ── Returns ──
public record ReturnSummaryDto(
    Guid ReturnId,
    string? RMANumber,
    string Status,
    string? ReturnReason,
    string? ReturnMethod,
    string? ReturnCarrier,
    string? ReturnTrackingNumber,
    DateOnly? ReturnByDate,
    int DaysUntilDeadline,
    IReadOnlyList<ReturnLineDto> Lines
);

public record ReturnDetailDto(
    Guid ReturnId,
    Guid OrderId,
    string ExternalOrderNumber,
    string? RMANumber,
    string Status,
    string? ReturnReason,
    string? ReturnMethod,
    string? ReturnCarrier,
    string? ReturnTrackingNumber,
    string? ReturnTrackingUrl,
    string? ReturnLabelBlobUrl,
    string? QRCodeBlobUrl,
    string? QRCodeData,
    string? DropOffLocation,
    string? DropOffAddress,
    DateOnly? ReturnByDate,
    int DaysUntilDeadline,
    DateOnly? ReceivedByRetailerDate,
    string? RejectionReason,
    RetailerSummaryDto? Retailer,
    IReadOnlyList<ReturnLineDto> Lines,
    RefundDto? Refund
);

public record ReturnLineDto(
    Guid OrderLineId,
    string ProductName,
    int Quantity,
    string? ReturnReason
);

// ── Refunds ──
public record RefundDto(
    Guid RefundId,
    decimal RefundAmount,
    string? Currency,
    string? RefundMethod,
    DateTime? RefundDate,
    string? EstimatedArrival,
    string? TransactionId
);

// ── Timeline ──
public record TimelineEventDto(
    Guid EventId,
    string EventType,
    DateTime EventDate,
    string Summary,
    string? EntityType,
    Guid? EntityId
);

// ── Review Queue ──
public record ReviewQueueItemDto(
    Guid EmailMessageId,
    string FromAddress,
    string? FromDisplayName,
    string Subject,
    DateTime ReceivedAt,
    string? BodyPreview,
    string? ClassificationType,
    decimal? ClassificationConfidence,
    string ProcessingStatus
);

public record ApproveReviewRequest(
    string? CorrectedClassification,
    string? CorrectedParsedDataJson
);
