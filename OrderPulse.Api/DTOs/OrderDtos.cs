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

/// <summary>
/// Flattened DTO: one row per order line, with order-level fields repeated.
/// Used by the "All Orders" table to show one row per item.
/// </summary>
public record OrderLineListItemDto(
    Guid OrderId,
    Guid OrderLineId,
    string? RetailerName,
    string ExternalOrderNumber,
    string ProductName,
    int Quantity,
    DateTime OrderDate,
    decimal? LineTotal,
    string LineStatus,
    string OrderStatus
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

public record ReviewDetailDto(
    Guid EmailMessageId,
    string FromAddress,
    string? FromDisplayName,
    string Subject,
    DateTime ReceivedAt,
    string? BodyPreview,
    string? BodyHtml,
    string? ClassificationType,
    decimal? ClassificationConfidence,
    string ProcessingStatus,
    string? ErrorDetails
);

public record ApproveReviewRequest(
    string? CorrectedClassification,
    string? CorrectedParsedDataJson
);

// ── Settings ──
public record TenantSettingsDto(
    string? ConnectedEmail,
    DateTime? LastSyncAt,
    string MailboxStatus,
    int PollingIntervalMinutes,
    bool WebhookEnabled,
    bool NotifyDelivery,
    bool NotifyShipment,
    bool NotifyReturn,
    bool NotifyRefund,
    bool NotifyIssues
);

public record TenantSettingsUpdateDto(
    int PollingIntervalMinutes,
    bool WebhookEnabled,
    bool NotifyDelivery,
    bool NotifyShipment,
    bool NotifyReturn,
    bool NotifyRefund,
    bool NotifyIssues
);

public record HistoricalImportRequestDto(
    DateTime StartDate,
    DateTime EndDate
);

public record HistoricalImportResultDto(
    int EmailsQueued,
    string? Message
);

// ── Inventory ──
public record InventoryItemDto(
    Guid InventoryItemId,
    Guid OrderLineId,
    Guid OrderId,
    string ProductName,
    string ItemCategory,
    int QuantityOnHand,
    string? UnitStatus,
    string? Condition,
    DateTime? PurchaseDate,
    DateTime? DeliveryDate,
    string? ExternalOrderNumber,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record InventoryItemDetailDto(
    Guid InventoryItemId,
    Guid OrderLineId,
    Guid OrderId,
    string ProductName,
    string ItemCategory,
    int QuantityOnHand,
    string? UnitStatus,
    string? Condition,
    DateTime? PurchaseDate,
    DateTime? DeliveryDate,
    string? ExternalOrderNumber,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<InventoryAdjustmentDto> RecentAdjustments
);

public record InventoryAdjustmentDto(
    Guid AdjustmentId,
    int QuantityDelta,
    int PreviousQuantity,
    int NewQuantity,
    string Reason,
    string? Notes,
    string? AdjustedBy,
    DateTime AdjustedAt
);

public record AdjustInventoryRequest(
    int QuantityDelta,
    string Reason,
    string? Notes
);

public record UpdateInventoryCategoryRequest(
    string Category
);

public record UpdateInventoryStatusRequest(
    string? UnitStatus,
    string? Condition
);

public record RelatedOrderDto(Guid OrderId, string ExternalOrderNumber, DateTime OrderDate, string Status, decimal? TotalAmount, string? Currency, string? RetailerName, IReadOnlyList<RelatedOrderLineDto> MatchingLines);
public record RelatedOrderLineDto(string ProductName, int Quantity, decimal? UnitPrice, decimal? LineTotal);
