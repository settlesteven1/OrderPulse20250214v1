using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.AI.Parsers;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Routes classified emails to the appropriate parser, writes parsed data to the database,
/// recalculates order status, and creates timeline events.
/// </summary>
public class EmailProcessingOrchestrator : IEmailProcessingOrchestrator
{
    private readonly ILogger<EmailProcessingOrchestrator> _logger;
    private readonly OrderPulseDbContext _db;
    private readonly RetailerMatcher _retailerMatcher;
    private readonly OrderStateMachine _stateMachine;

    // Parsers
    private readonly IEmailParser<OrderParserResult> _orderParser;
    private readonly IEmailParser<ShipmentParserResult> _shipmentParser;
    private readonly IEmailParser<DeliveryParserResult> _deliveryParser;
    private readonly IEmailParser<ReturnParserResult> _returnParser;
    private readonly IEmailParser<RefundParserResult> _refundParser;
    private readonly IEmailParser<CancellationParserResult> _cancellationParser;
    private readonly IEmailParser<PaymentParserResult> _paymentParser;

    public EmailProcessingOrchestrator(
        ILogger<EmailProcessingOrchestrator> logger,
        OrderPulseDbContext db,
        RetailerMatcher retailerMatcher,
        OrderStateMachine stateMachine,
        IEmailParser<OrderParserResult> orderParser,
        IEmailParser<ShipmentParserResult> shipmentParser,
        IEmailParser<DeliveryParserResult> deliveryParser,
        IEmailParser<ReturnParserResult> returnParser,
        IEmailParser<RefundParserResult> refundParser,
        IEmailParser<CancellationParserResult> cancellationParser,
        IEmailParser<PaymentParserResult> paymentParser)
    {
        _logger = logger;
        _db = db;
        _retailerMatcher = retailerMatcher;
        _stateMachine = stateMachine;
        _orderParser = orderParser;
        _shipmentParser = shipmentParser;
        _deliveryParser = deliveryParser;
        _returnParser = returnParser;
        _refundParser = refundParser;
        _cancellationParser = cancellationParser;
        _paymentParser = paymentParser;
    }

    public async Task ProcessEmailAsync(Guid emailMessageId, CancellationToken ct = default)
    {
        var email = await _db.EmailMessages.FindAsync(new object[] { emailMessageId }, ct);
        if (email is null)
        {
            _logger.LogWarning("Email {id} not found for processing", emailMessageId);
            return;
        }

        if (email.ClassificationType is null)
        {
            _logger.LogWarning("Email {id} has no classification type", emailMessageId);
            return;
        }

        _logger.LogInformation("Processing email {id} classified as {type}",
            emailMessageId, email.ClassificationType);

        try
        {
            email.ProcessingStatus = ProcessingStatus.Parsing;
            await _db.SaveChangesAsync(ct);

            // Match retailer from sender address
            var retailer = await _retailerMatcher.MatchAsync(email.FromAddress, ct);
            var retailerContext = retailer?.Name;

            // TODO: Retrieve full body from Blob Storage using email.BodyBlobUrl
            var body = email.BodyPreview ?? "";

            // Route to the appropriate parser
            var orderId = await RouteAndProcessAsync(email, body, retailerContext, ct);

            // Recalculate order status if we have an order
            if (orderId.HasValue)
            {
                await _stateMachine.RecalculateStatusAsync(orderId.Value, ct);
            }

            email.ProcessingStatus = ProcessingStatus.Parsed;
            email.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Successfully processed email {id}", emailMessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process email {id}", emailMessageId);
            email.ProcessingStatus = ProcessingStatus.Failed;
            email.ErrorDetails = ex.Message;
            email.RetryCount++;
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<Guid?> RouteAndProcessAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        return email.ClassificationType switch
        {
            EmailClassificationType.OrderConfirmation or
            EmailClassificationType.OrderModification
                => await ProcessOrderAsync(email, body, retailerContext, ct),

            EmailClassificationType.ShipmentConfirmation or
            EmailClassificationType.ShipmentUpdate
                => await ProcessShipmentAsync(email, body, retailerContext, ct),

            EmailClassificationType.DeliveryConfirmation or
            EmailClassificationType.DeliveryIssue
                => await ProcessDeliveryAsync(email, body, retailerContext, ct),

            EmailClassificationType.ReturnInitiation or
            EmailClassificationType.ReturnLabel or
            EmailClassificationType.ReturnReceived or
            EmailClassificationType.ReturnRejection
                => await ProcessReturnAsync(email, body, retailerContext, ct),

            EmailClassificationType.RefundConfirmation
                => await ProcessRefundAsync(email, body, retailerContext, ct),

            EmailClassificationType.OrderCancellation
                => await ProcessCancellationAsync(email, body, retailerContext, ct),

            EmailClassificationType.PaymentConfirmation
                => await ProcessPaymentAsync(email, body, retailerContext, ct),

            EmailClassificationType.Promotional => null, // Should not reach here

            _ => throw new InvalidOperationException(
                $"Unknown classification type: {email.ClassificationType}")
        };
    }

    // ── Order Confirmation / Modification ──

    private async Task<Guid?> ProcessOrderAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        var result = await _orderParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);
        if (result.Data?.Order is null)
        {
            await FlagForReview(email, "Order parser returned no data", ct);
            return null;
        }

        if (result.NeedsReview)
        {
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return null;
        }

        var parsed = result.Data.Order;
        var retailer = await _retailerMatcher.MatchAsync(email.FromAddress, ct);

        // Try to find existing order by order number
        var existingOrder = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ExternalOrderNumber == parsed.ExternalOrderNumber, ct);

        if (existingOrder is not null && parsed.IsModification)
        {
            // Update existing order
            UpdateOrderFromParsed(existingOrder, parsed);
            existingOrder.LastUpdatedEmailId = email.EmailMessageId;
            existingOrder.UpdatedAt = DateTime.UtcNow;

            await CreateTimelineEvent(existingOrder.OrderId, email, "OrderModified",
                "Order modified", $"Order updated from email: {email.Subject}", ct);

            await _db.SaveChangesAsync(ct);
            return existingOrder.OrderId;
        }

        if (existingOrder is not null)
        {
            // Duplicate order confirmation — link email but don't create new order
            existingOrder.LastUpdatedEmailId = email.EmailMessageId;
            await _db.SaveChangesAsync(ct);
            return existingOrder.OrderId;
        }

        // Create new order
        var order = new Order
        {
            OrderId = Guid.NewGuid(),
            TenantId = email.TenantId,
            RetailerId = retailer?.RetailerId,
            ExternalOrderNumber = parsed.ExternalOrderNumber,
            ExternalOrderUrl = parsed.ExternalOrderUrl,
            OrderDate = TryParseDate(parsed.OrderDate) ?? email.ReceivedAt,
            Status = OrderStatus.Placed,
            Subtotal = parsed.Subtotal,
            TaxAmount = parsed.TaxAmount,
            ShippingCost = parsed.ShippingCost,
            DiscountAmount = parsed.DiscountAmount,
            TotalAmount = parsed.TotalAmount,
            Currency = parsed.Currency,
            EstimatedDeliveryStart = TryParseDateOnly(parsed.EstimatedDeliveryStart),
            EstimatedDeliveryEnd = TryParseDateOnly(parsed.EstimatedDeliveryEnd),
            ShippingAddress = parsed.ShippingAddress,
            PaymentMethodSummary = parsed.PaymentMethodSummary,
            SourceEmailId = email.EmailMessageId,
            LastUpdatedEmailId = email.EmailMessageId
        };

        // Add line items
        int lineNum = 1;
        foreach (var line in result.Data.Lines)
        {
            order.Lines.Add(new OrderLine
            {
                OrderLineId = Guid.NewGuid(),
                OrderId = order.OrderId,
                LineNumber = lineNum++,
                ProductName = line.ProductName,
                ProductUrl = line.ProductUrl,
                SKU = line.Sku,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                LineTotal = line.LineTotal,
                ImageUrl = line.ImageUrl,
                Status = OrderLineStatus.Ordered
            });
        }

        _db.Orders.Add(order);

        await CreateTimelineEvent(order.OrderId, email, "OrderPlaced",
            "Order placed", $"Order #{parsed.ExternalOrderNumber} confirmed", ct);

        await _db.SaveChangesAsync(ct);
        return order.OrderId;
    }

    // ── Shipment Confirmation / Update ──

    private async Task<Guid?> ProcessShipmentAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        var result = await _shipmentParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);
        if (result.Data?.Shipments is null || result.Data.Shipments.Count == 0)
        {
            await FlagForReview(email, "Shipment parser returned no data", ct);
            return null;
        }

        if (result.NeedsReview)
        {
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return null;
        }

        Guid? lastOrderId = null;

        foreach (var shipmentData in result.Data.Shipments)
        {
            // Find the order this shipment belongs to
            var order = await FindOrderByReference(shipmentData.OrderReference, email.TenantId, ct);
            if (order is null)
            {
                _logger.LogWarning("Could not match shipment to order: {orderRef}", shipmentData.OrderReference);
                continue;
            }

            // Check for existing shipment by tracking number
            Shipment? shipment = null;
            if (!string.IsNullOrEmpty(shipmentData.TrackingNumber))
            {
                shipment = await _db.Shipments
                    .FirstOrDefaultAsync(s => s.OrderId == order.OrderId &&
                        s.TrackingNumber == shipmentData.TrackingNumber, ct);
            }

            var shipmentStatus = ParseShipmentStatus(shipmentData.Status);

            if (shipment is not null)
            {
                // Update existing shipment
                shipment.Status = shipmentStatus;
                shipment.LastStatusUpdate = shipmentData.StatusDetail;
                shipment.LastStatusDate = DateTime.UtcNow;
                if (shipmentData.EstimatedDelivery is not null)
                    shipment.EstimatedDelivery = TryParseDateOnly(shipmentData.EstimatedDelivery);
                shipment.UpdatedAt = DateTime.UtcNow;

                await CreateTimelineEvent(order.OrderId, email, "ShipmentUpdated",
                    $"Shipment updated: {shipmentStatus}", shipmentData.StatusDetail, ct);
            }
            else
            {
                // Create new shipment
                shipment = new Shipment
                {
                    ShipmentId = Guid.NewGuid(),
                    TenantId = email.TenantId,
                    OrderId = order.OrderId,
                    Carrier = shipmentData.Carrier,
                    CarrierNormalized = shipmentData.CarrierNormalized,
                    TrackingNumber = shipmentData.TrackingNumber,
                    TrackingUrl = shipmentData.TrackingUrl,
                    ShipDate = TryParseDate(shipmentData.ShipDate),
                    EstimatedDelivery = TryParseDateOnly(shipmentData.EstimatedDelivery),
                    Status = shipmentStatus,
                    SourceEmailId = email.EmailMessageId
                };
                _db.Shipments.Add(shipment);

                // Link shipment lines to order lines by product name match
                foreach (var item in shipmentData.Items)
                {
                    var orderLine = order.Lines?.FirstOrDefault(l =>
                        l.ProductName.Contains(item.ProductName, StringComparison.OrdinalIgnoreCase));
                    if (orderLine is not null)
                    {
                        _db.ShipmentLines.Add(new ShipmentLine
                        {
                            ShipmentLineId = Guid.NewGuid(),
                            ShipmentId = shipment.ShipmentId,
                            OrderLineId = orderLine.OrderLineId,
                            Quantity = item.Quantity
                        });
                    }
                }

                await CreateTimelineEvent(order.OrderId, email, "ShipmentCreated",
                    $"Shipped via {shipmentData.Carrier ?? "unknown carrier"}",
                    $"Tracking: {shipmentData.TrackingNumber}", ct);
            }

            order.LastUpdatedEmailId = email.EmailMessageId;
            lastOrderId = order.OrderId;
        }

        await _db.SaveChangesAsync(ct);
        return lastOrderId;
    }

    // ── Delivery Confirmation / Issue ──

    private async Task<Guid?> ProcessDeliveryAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        var result = await _deliveryParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);
        if (result.Data?.Delivery is null)
        {
            await FlagForReview(email, "Delivery parser returned no data", ct);
            return null;
        }

        if (result.NeedsReview)
        {
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return null;
        }

        var parsed = result.Data.Delivery;

        // Find the shipment by tracking number, or find the order by reference
        Shipment? shipment = null;
        Order? order = null;

        if (!string.IsNullOrEmpty(parsed.TrackingNumber))
        {
            shipment = await _db.Shipments
                .Include(s => s.Order)
                .FirstOrDefaultAsync(s => s.TrackingNumber == parsed.TrackingNumber, ct);
            order = shipment?.Order;
        }

        if (order is null && !string.IsNullOrEmpty(parsed.OrderReference))
        {
            order = await FindOrderByReference(parsed.OrderReference, email.TenantId, ct);
            shipment = order is not null
                ? await _db.Shipments.FirstOrDefaultAsync(s => s.OrderId == order.OrderId, ct)
                : null;
        }

        if (shipment is null)
        {
            _logger.LogWarning("Could not match delivery to shipment for email {id}", email.EmailMessageId);
            await FlagForReview(email, "Could not match delivery to existing shipment", ct);
            return order?.OrderId;
        }

        var deliveryStatus = ParseDeliveryStatus(parsed.Status);
        var issueType = ParseDeliveryIssueType(parsed.IssueType);

        // Check for existing delivery on this shipment
        var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.ShipmentId == shipment.ShipmentId, ct);

        if (delivery is not null)
        {
            delivery.Status = deliveryStatus;
            delivery.IssueType = issueType;
            delivery.IssueDescription = parsed.IssueDescription;
            delivery.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            delivery = new Delivery
            {
                DeliveryId = Guid.NewGuid(),
                TenantId = email.TenantId,
                ShipmentId = shipment.ShipmentId,
                DeliveryDate = TryParseDate(parsed.DeliveryDate),
                DeliveryLocation = parsed.DeliveryLocation ?? parsed.SignedBy,
                Status = deliveryStatus,
                IssueType = issueType,
                IssueDescription = parsed.IssueDescription,
                PhotoBlobUrl = parsed.PhotoUrl,
                SourceEmailId = email.EmailMessageId
            };
            _db.Deliveries.Add(delivery);
        }

        // Update shipment status
        shipment.Status = deliveryStatus == DeliveryStatus.Delivered
            ? ShipmentStatus.Delivered
            : ShipmentStatus.Exception;
        shipment.UpdatedAt = DateTime.UtcNow;

        var eventType = deliveryStatus == DeliveryStatus.Delivered ? "Delivered" : "DeliveryIssue";
        var summary = deliveryStatus == DeliveryStatus.Delivered
            ? $"Delivered to {parsed.DeliveryLocation ?? "address"}"
            : $"Delivery issue: {parsed.IssueType}";

        if (order is not null)
        {
            order.LastUpdatedEmailId = email.EmailMessageId;
            await CreateTimelineEvent(order.OrderId, email, eventType, summary, parsed.IssueDescription, ct);
        }

        await _db.SaveChangesAsync(ct);
        return order?.OrderId;
    }

    // ── Return (Initiation, Label, Received, Rejection) ──

    private async Task<Guid?> ProcessReturnAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        var result = await _returnParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);
        if (result.Data?.Return is null)
        {
            await FlagForReview(email, "Return parser returned no data", ct);
            return null;
        }

        if (result.NeedsReview)
        {
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return null;
        }

        var parsed = result.Data.Return;

        // Find the order
        var order = await FindOrderByReference(parsed.OrderReference, email.TenantId, ct);
        if (order is null)
        {
            _logger.LogWarning("Could not match return to order: {orderRef}", parsed.OrderReference);
            await FlagForReview(email, $"Could not match return to order: {parsed.OrderReference}", ct);
            return null;
        }

        // Check for existing return by RMA number or order
        Return? returnEntity = null;
        if (!string.IsNullOrEmpty(parsed.RmaNumber))
        {
            returnEntity = await _db.Returns
                .Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.OrderId == order.OrderId && r.RMANumber == parsed.RmaNumber, ct);
        }

        var returnStatus = ParseReturnStatus(parsed.Status);

        if (returnEntity is not null)
        {
            // Update existing return
            returnEntity.Status = returnStatus;
            if (parsed.ReturnTrackingNumber is not null)
                returnEntity.ReturnTrackingNumber = parsed.ReturnTrackingNumber;
            if (parsed.ReturnTrackingUrl is not null)
                returnEntity.ReturnTrackingUrl = parsed.ReturnTrackingUrl;
            if (parsed.ReturnByDate is not null)
                returnEntity.ReturnByDate = TryParseDateOnly(parsed.ReturnByDate);
            if (parsed.ReceivedByRetailerDate is not null)
                returnEntity.ReceivedByRetailerDate = TryParseDateOnly(parsed.ReceivedByRetailerDate);
            if (parsed.RejectionReason is not null)
                returnEntity.RejectionReason = parsed.RejectionReason;
            if (parsed.QrCodeInEmail)
                returnEntity.QRCodeData = "QR code provided in email";
            if (parsed.DropOffLocation is not null)
                returnEntity.DropOffLocation = parsed.DropOffLocation;
            if (parsed.DropOffAddress is not null)
                returnEntity.DropOffAddress = parsed.DropOffAddress;
            returnEntity.LastUpdatedEmailId = email.EmailMessageId;
            returnEntity.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            // Create new return
            var returnMethod = ParseReturnMethod(parsed.ReturnMethod);
            returnEntity = new Return
            {
                ReturnId = Guid.NewGuid(),
                TenantId = email.TenantId,
                OrderId = order.OrderId,
                RMANumber = parsed.RmaNumber,
                Status = returnStatus,
                ReturnReason = parsed.ReturnReason,
                ReturnMethod = returnMethod,
                ReturnCarrier = parsed.ReturnCarrier,
                ReturnTrackingNumber = parsed.ReturnTrackingNumber,
                ReturnTrackingUrl = parsed.ReturnTrackingUrl,
                ReturnByDate = TryParseDateOnly(parsed.ReturnByDate),
                ReceivedByRetailerDate = TryParseDateOnly(parsed.ReceivedByRetailerDate),
                RejectionReason = parsed.RejectionReason,
                DropOffLocation = parsed.DropOffLocation,
                DropOffAddress = parsed.DropOffAddress,
                SourceEmailId = email.EmailMessageId,
                LastUpdatedEmailId = email.EmailMessageId
            };

            if (parsed.QrCodeInEmail)
                returnEntity.QRCodeData = "QR code provided in email";

            // Link return lines to order lines
            foreach (var item in result.Data.Items)
            {
                var orderLine = order.Lines?.FirstOrDefault(l =>
                    l.ProductName.Contains(item.ProductName, StringComparison.OrdinalIgnoreCase));
                if (orderLine is not null)
                {
                    returnEntity.Lines.Add(new ReturnLine
                    {
                        ReturnLineId = Guid.NewGuid(),
                        ReturnId = returnEntity.ReturnId,
                        OrderLineId = orderLine.OrderLineId,
                        Quantity = item.Quantity,
                        ReturnReason = item.ReturnReason
                    });

                    orderLine.Status = OrderLineStatus.ReturnInitiated;
                }
            }

            _db.Returns.Add(returnEntity);
        }

        order.LastUpdatedEmailId = email.EmailMessageId;

        var eventSummary = parsed.Subtype switch
        {
            "ReturnLabel" => $"Return label issued (RMA: {parsed.RmaNumber})",
            "ReturnReceived" => "Return received by retailer",
            "ReturnRejection" => $"Return rejected: {parsed.RejectionReason}",
            _ => $"Return initiated (RMA: {parsed.RmaNumber})"
        };

        await CreateTimelineEvent(order.OrderId, email, $"Return{parsed.Subtype ?? "Initiated"}",
            eventSummary, parsed.ReturnReason, ct);

        await _db.SaveChangesAsync(ct);
        return order.OrderId;
    }

    // ── Refund Confirmation ──

    private async Task<Guid?> ProcessRefundAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        var result = await _refundParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);
        if (result.Data?.Refund is null)
        {
            await FlagForReview(email, "Refund parser returned no data", ct);
            return null;
        }

        if (result.NeedsReview)
        {
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return null;
        }

        var parsed = result.Data.Refund;

        var order = await FindOrderByReference(parsed.OrderReference, email.TenantId, ct);
        if (order is null)
        {
            _logger.LogWarning("Could not match refund to order: {orderRef}", parsed.OrderReference);
            await FlagForReview(email, $"Could not match refund to order: {parsed.OrderReference}", ct);
            return null;
        }

        // Find associated return by RMA if available
        Return? associatedReturn = null;
        if (!string.IsNullOrEmpty(parsed.ReturnRma))
        {
            associatedReturn = await _db.Returns
                .FirstOrDefaultAsync(r => r.OrderId == order.OrderId && r.RMANumber == parsed.ReturnRma, ct);
        }

        var refund = new Refund
        {
            RefundId = Guid.NewGuid(),
            TenantId = email.TenantId,
            OrderId = order.OrderId,
            ReturnId = associatedReturn?.ReturnId,
            RefundAmount = parsed.RefundAmount,
            Currency = parsed.Currency,
            RefundMethod = parsed.RefundMethod,
            RefundDate = TryParseDate(parsed.RefundDate),
            EstimatedArrival = parsed.EstimatedArrival,
            TransactionId = parsed.TransactionId,
            SourceEmailId = email.EmailMessageId
        };
        _db.Refunds.Add(refund);

        // Update return status if linked
        if (associatedReturn is not null)
        {
            associatedReturn.Status = ReturnStatus.Refunded;
            associatedReturn.UpdatedAt = DateTime.UtcNow;
        }

        order.LastUpdatedEmailId = email.EmailMessageId;

        await CreateTimelineEvent(order.OrderId, email, "RefundIssued",
            $"Refund of {parsed.RefundAmount:C} issued",
            $"Method: {parsed.RefundMethod}, ETA: {parsed.EstimatedArrival}", ct);

        await _db.SaveChangesAsync(ct);
        return order.OrderId;
    }

    // ── Order Cancellation ──

    private async Task<Guid?> ProcessCancellationAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        var result = await _cancellationParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);
        if (result.Data?.Cancellation is null)
        {
            await FlagForReview(email, "Cancellation parser returned no data", ct);
            return null;
        }

        if (result.NeedsReview)
        {
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return null;
        }

        var parsed = result.Data.Cancellation;

        var order = await FindOrderByReference(parsed.OrderReference, email.TenantId, ct);
        if (order is null)
        {
            _logger.LogWarning("Could not match cancellation to order: {orderRef}", parsed.OrderReference);
            await FlagForReview(email, $"Could not match cancellation to order: {parsed.OrderReference}", ct);
            return null;
        }

        // Mark cancelled items
        foreach (var cancelledItem in result.Data.CancelledItems)
        {
            var orderLine = order.Lines?.FirstOrDefault(l =>
                l.ProductName.Contains(cancelledItem.ProductName, StringComparison.OrdinalIgnoreCase));
            if (orderLine is not null)
            {
                orderLine.Status = OrderLineStatus.Cancelled;
                orderLine.UpdatedAt = DateTime.UtcNow;
            }
        }

        order.LastUpdatedEmailId = email.EmailMessageId;

        var summary = parsed.IsFullCancellation
            ? "Order cancelled"
            : $"Partial cancellation: {result.Data.CancelledItems.Count} item(s)";

        await CreateTimelineEvent(order.OrderId, email, "OrderCancelled",
            summary, parsed.CancellationReason, ct);

        // If there's an inline refund, create the refund record too
        if (parsed.RefundAmount.HasValue && parsed.RefundAmount > 0)
        {
            _db.Refunds.Add(new Refund
            {
                RefundId = Guid.NewGuid(),
                TenantId = email.TenantId,
                OrderId = order.OrderId,
                RefundAmount = parsed.RefundAmount.Value,
                RefundMethod = parsed.RefundMethod,
                EstimatedArrival = parsed.RefundTimeline,
                SourceEmailId = email.EmailMessageId
            });
        }

        await _db.SaveChangesAsync(ct);
        return order.OrderId;
    }

    // ── Payment Confirmation ──

    private async Task<Guid?> ProcessPaymentAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        var result = await _paymentParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);
        if (result.Data?.Payment is null)
        {
            await FlagForReview(email, "Payment parser returned no data", ct);
            return null;
        }

        if (result.NeedsReview)
        {
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return null;
        }

        var parsed = result.Data.Payment;

        // Try to link to an existing order
        var order = await FindOrderByReference(parsed.OrderReference, email.TenantId, ct);
        if (order is not null)
        {
            order.PaymentMethodSummary = parsed.PaymentMethod;
            order.LastUpdatedEmailId = email.EmailMessageId;

            await CreateTimelineEvent(order.OrderId, email, "PaymentConfirmed",
                $"Payment of {parsed.Amount:C} confirmed",
                $"Method: {parsed.PaymentMethod}", ct);

            await _db.SaveChangesAsync(ct);
            return order.OrderId;
        }

        // Payment without matching order — just log it
        _logger.LogInformation("Payment confirmation without matching order: {orderRef}", parsed.OrderReference);
        return null;
    }

    // ── Helper Methods ──

    private async Task<Order?> FindOrderByReference(string? orderReference, Guid tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderReference))
            return null;

        // Normalize: strip leading #, trim whitespace
        var normalized = orderReference.TrimStart('#').Trim();

        // Try exact match first
        var order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ExternalOrderNumber == orderReference, ct);

        if (order is not null) return order;

        // Try without leading #
        order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ExternalOrderNumber == normalized, ct);

        if (order is not null) return order;

        // Try contains match (for partial order numbers)
        order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ExternalOrderNumber.Contains(normalized), ct);

        return order;
    }

    private async Task CreateTimelineEvent(
        Guid orderId, EmailMessage email, string eventType,
        string summary, string? details, CancellationToken ct)
    {
        _db.OrderEvents.Add(new OrderEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = email.TenantId,
            OrderId = orderId,
            EventType = eventType,
            EventDate = DateTime.UtcNow,
            Summary = summary,
            Details = details,
            EmailMessageId = email.EmailMessageId
        });
    }

    private async Task FlagForReview(EmailMessage email, string reason, CancellationToken ct)
    {
        _logger.LogWarning("Flagging email {id} for review: {reason}", email.EmailMessageId, reason);
        email.ProcessingStatus = ProcessingStatus.ManualReview;
        email.ErrorDetails = reason;
        await _db.SaveChangesAsync(ct);
    }

    private void UpdateOrderFromParsed(Order order, OrderData parsed)
    {
        if (parsed.TotalAmount.HasValue) order.TotalAmount = parsed.TotalAmount;
        if (parsed.Subtotal.HasValue) order.Subtotal = parsed.Subtotal;
        if (parsed.TaxAmount.HasValue) order.TaxAmount = parsed.TaxAmount;
        if (parsed.ShippingCost.HasValue) order.ShippingCost = parsed.ShippingCost;
        if (parsed.DiscountAmount.HasValue) order.DiscountAmount = parsed.DiscountAmount;
        if (parsed.ShippingAddress is not null) order.ShippingAddress = parsed.ShippingAddress;
        if (parsed.EstimatedDeliveryStart is not null)
            order.EstimatedDeliveryStart = TryParseDateOnly(parsed.EstimatedDeliveryStart);
        if (parsed.EstimatedDeliveryEnd is not null)
            order.EstimatedDeliveryEnd = TryParseDateOnly(parsed.EstimatedDeliveryEnd);
    }

    // ── Enum Parsing Helpers ──

    private static ShipmentStatus ParseShipmentStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "shipped" => ShipmentStatus.Shipped,
            "intransit" => ShipmentStatus.InTransit,
            "outfordelivery" => ShipmentStatus.OutForDelivery,
            "delivered" => ShipmentStatus.Delivered,
            "exception" => ShipmentStatus.Exception,
            _ => ShipmentStatus.Shipped
        };

    private static DeliveryStatus ParseDeliveryStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "delivered" => DeliveryStatus.Delivered,
            "attempteddelivery" => DeliveryStatus.AttemptedDelivery,
            "deliveryexception" => DeliveryStatus.DeliveryException,
            "lost" => DeliveryStatus.Lost,
            _ => DeliveryStatus.Delivered
        };

    private static DeliveryIssueType? ParseDeliveryIssueType(string? issueType) =>
        issueType?.ToLowerInvariant() switch
        {
            "missing" => DeliveryIssueType.Missing,
            "damaged" => DeliveryIssueType.Damaged,
            "wrongitem" => DeliveryIssueType.WrongItem,
            "notreceived" => DeliveryIssueType.NotReceived,
            "stolen" => DeliveryIssueType.Stolen,
            "other" => DeliveryIssueType.Other,
            null => null,
            _ => DeliveryIssueType.Other
        };

    private static ReturnStatus ParseReturnStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "initiated" => ReturnStatus.Initiated,
            "labelissued" => ReturnStatus.LabelIssued,
            "shipped" => ReturnStatus.Shipped,
            "received" => ReturnStatus.Received,
            "rejected" => ReturnStatus.Rejected,
            "refundpending" => ReturnStatus.RefundPending,
            "refunded" => ReturnStatus.Refunded,
            _ => ReturnStatus.Initiated
        };

    private static ReturnMethod? ParseReturnMethod(string? method) =>
        method?.ToLowerInvariant() switch
        {
            "mail" => ReturnMethod.Mail,
            "dropoff" => ReturnMethod.DropOff,
            "pickup" => ReturnMethod.Pickup,
            _ => null
        };

    // ── Date Parsing Helpers ──

    private static DateTime? TryParseDate(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        return DateTime.TryParse(dateStr, out var result) ? result.ToUniversalTime() : null;
    }

    private static DateOnly? TryParseDateOnly(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        if (DateOnly.TryParse(dateStr, out var result)) return result;
        if (DateTime.TryParse(dateStr, out var dt)) return DateOnly.FromDateTime(dt);
        return null;
    }
}
