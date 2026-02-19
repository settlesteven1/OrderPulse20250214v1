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
    private readonly EmailBlobStorageService _blobStorage;
    private readonly ProcessingLogger _log;

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
        EmailBlobStorageService blobStorage,
        ProcessingLogger log,
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
        _blobStorage = blobStorage;
        _log = log;
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
        await _log.Info(emailMessageId, "Start", "Beginning email processing");

        // Use IgnoreQueryFilters to find email regardless of RLS
        var email = await _db.EmailMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.EmailMessageId == emailMessageId, ct);
        if (email is null)
        {
            await _log.Error(emailMessageId, "Start", "Email not found in database");
            return;
        }

        // Set SESSION_CONTEXT for all subsequent DB operations
        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
            email.TenantId.ToString());
        await _log.Info(emailMessageId, "Start", $"SESSION_CONTEXT set to {email.TenantId}");

        await _log.Info(emailMessageId, "Start",
            $"Subject: {email.Subject}",
            $"From: {email.FromAddress}, Classification: {email.ClassificationType}, BlobUrl: {email.BodyBlobUrl ?? "NONE"}");

        if (email.ClassificationType is null)
        {
            await _log.Error(emailMessageId, "Start", "No classification type set");
            return;
        }

        try
        {
            email.ProcessingStatus = ProcessingStatus.Parsing;
            await _db.SaveChangesAsync(ct);

            // Match retailer
            var retailer = await _retailerMatcher.MatchAsync(email.FromAddress, email.OriginalFromAddress, ct);
            var retailerContext = retailer?.Name;
            var matchSource = retailer != null
                ? $"Matched retailer: {retailer.Name}"
                : $"No retailer match for {email.FromAddress}" +
                  (email.OriginalFromAddress != null ? $" (original: {email.OriginalFromAddress})" : "");
            await _log.Info(emailMessageId, "RetailerMatch", matchSource);

            // Retrieve full body from Blob Storage, fall back to preview
            var body = email.BodyPreview ?? "";
            var bodySource = "preview";
            if (!string.IsNullOrEmpty(email.BodyBlobUrl))
            {
                try
                {
                    var fullBody = await _blobStorage.GetEmailBodyAsync(email.BodyBlobUrl, ct);
                    if (!string.IsNullOrEmpty(fullBody))
                    {
                        body = fullBody;
                        bodySource = "blob";
                    }
                    else
                    {
                        await _log.Warn(emailMessageId, "BlobFetch", "Blob returned null/empty, using preview");
                    }
                }
                catch (Exception blobEx)
                {
                    await _log.Error(emailMessageId, "BlobFetch", $"Blob fetch failed: {blobEx.Message}", email.BodyBlobUrl);
                }
            }
            else
            {
                await _log.Warn(emailMessageId, "BlobFetch", "No BodyBlobUrl set, using preview only");
            }

            await _log.Info(emailMessageId, "BodyRetrieved",
                $"Source: {bodySource}, Length: {body.Length} chars",
                body.Length > 1000 ? body[..1000] : body);

            // Route to the appropriate parser
            var orderIds = await RouteAndProcessAsync(email, body, retailerContext, ct);

            if (orderIds.Count > 0)
            {
                foreach (var oid in orderIds)
                {
                    await _log.Success(emailMessageId, "OrderCreated", $"Order ID: {oid}");
                    await _stateMachine.RecalculateStatusAsync(oid, ct);
                }
            }
            else
            {
                await _log.Warn(emailMessageId, "RouteResult", "No order ID returned from parser");
            }

            email.ProcessingStatus = ProcessingStatus.Parsed;
            email.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _log.Success(emailMessageId, "Complete", "Email processing finished successfully");
        }
        catch (Exception ex)
        {
            var innerMsg = ex.InnerException?.Message ?? "no inner exception";
            var innerInner = ex.InnerException?.InnerException?.Message;
            var fullError = $"{ex.GetType().Name}: {ex.Message} | Inner: {innerMsg}" +
                (innerInner != null ? $" | InnerInner: {innerInner}" : "");
            await _log.Error(emailMessageId, "Fatal", fullError, ex.StackTrace);
            email.ProcessingStatus = ProcessingStatus.Failed;
            email.ErrorDetails = ex.Message;
            email.RetryCount++;
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<List<Guid>> RouteAndProcessAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        // ProcessOrderAsync now handles multi-order internally and returns all created order IDs
        if (email.ClassificationType is EmailClassificationType.OrderConfirmation
            or EmailClassificationType.OrderModification)
        {
            return await ProcessOrderAsync(email, body, retailerContext, ct);
        }

        // All other types return a single order ID
        Guid? singleId = email.ClassificationType switch
        {
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

            EmailClassificationType.Promotional => null,

            _ => throw new InvalidOperationException(
                $"Unknown classification type: {email.ClassificationType}")
        };

        return singleId.HasValue ? new List<Guid> { singleId.Value } : new List<Guid>();
    }

    // ── Order Confirmation / Modification ──

    private async Task<List<Guid>> ProcessOrderAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        await _log.Info(email.EmailMessageId, "OrderParser", $"Calling order parser with {body.Length} chars body");
        var result = await _orderParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);

        if (result.Data is null)
        {
            await _log.Error(email.EmailMessageId, "OrderParser", "Order parser returned null data — flagging for review");
            await FlagForReview(email, "Order parser returned no data", ct);
            return new List<Guid>();
        }

        // Normalize into a list of orders (handles both single and multi-order emails)
        var allOrders = result.Data.GetAllOrders();

        await _log.Info(email.EmailMessageId, "OrderParser",
            $"Parser returned: Confidence={result.Confidence}, NeedsReview={result.NeedsReview}, OrderCount={allOrders.Count}",
            $"ErrorMessage: {result.ErrorMessage ?? "none"}, OrderNumbers: {string.Join(", ", allOrders.Select(o => o.Order.ExternalOrderNumber ?? "null"))}");

        if (allOrders.Count == 0)
        {
            await _log.Error(email.EmailMessageId, "OrderParser", "Order parser returned no orders — flagging for review");
            await FlagForReview(email, "Order parser returned no data", ct);
            return new List<Guid>();
        }

        if (result.NeedsReview)
        {
            await _log.Warn(email.EmailMessageId, "OrderParser", $"Low confidence {result.Confidence} — flagging for review");
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return new List<Guid>();
        }

        var retailer = await _retailerMatcher.MatchAsync(email.FromAddress, email.OriginalFromAddress, ct);
        var createdIds = new List<Guid>();

        foreach (var entry in allOrders)
        {
            var orderId = await ProcessSingleOrder(entry.Order, entry.Lines, email, retailer, ct);
            if (orderId.HasValue) createdIds.Add(orderId.Value);
        }

        if (allOrders.Count > 1)
        {
            await _log.Info(email.EmailMessageId, "OrderParser",
                $"Multi-order email: created/updated {allOrders.Count} orders, {createdIds.Count} IDs returned");
        }

        return createdIds;
    }

    /// <summary>
    /// Processes a single order from parsed data. Handles dedup, modification, and creation.
    /// </summary>
    private async Task<Guid?> ProcessSingleOrder(
        OrderData parsed, List<OrderLineData> lines, EmailMessage email,
        Retailer? retailer, CancellationToken ct)
    {
        // Try to find existing order by order number (normalize and bypass RLS)
        var normalized = !string.IsNullOrWhiteSpace(parsed.ExternalOrderNumber)
            ? NormalizeOrderNumber(parsed.ExternalOrderNumber) : null;

        var existingOrder = normalized != null
            ? await _db.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Lines)
                .Where(o => o.TenantId == email.TenantId)
                .FirstOrDefaultAsync(o => o.ExternalOrderNumber == normalized
                    || o.ExternalOrderNumber == parsed.ExternalOrderNumber, ct)
            : null;

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
            // Link email and add any new line items not already on the order
            existingOrder.LastUpdatedEmailId = email.EmailMessageId;
            AddNewLineItems(existingOrder, lines);
            await _db.SaveChangesAsync(ct);
            return existingOrder.OrderId;
        }

        // Create new order
        var order = new Order
        {
            OrderId = Guid.NewGuid(),
            TenantId = email.TenantId,
            RetailerId = retailer?.RetailerId,
            ExternalOrderNumber = normalized ?? parsed.ExternalOrderNumber,
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
        foreach (var line in lines)
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

        await _log.Info(email.EmailMessageId, "OrderCreated",
            $"Created order {order.OrderId} — #{order.ExternalOrderNumber} with {lines.Count} lines");

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
            // Find or create the order this shipment belongs to
            var order = await FindOrCreateOrderByReference(shipmentData.OrderReference, email, retailerContext, ct);

            // Check for existing shipment by tracking number
            Shipment? shipment = null;
            if (!string.IsNullOrEmpty(shipmentData.TrackingNumber))
            {
                shipment = await _db.Shipments
                    .IgnoreQueryFilters()
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
        await _log.Info(email.EmailMessageId, "DeliveryParser", $"Calling delivery parser with {body.Length} chars body");
        var result = await _deliveryParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);

        await _log.Info(email.EmailMessageId, "DeliveryParser",
            $"Parser returned: Confidence={result.Confidence}, NeedsReview={result.NeedsReview}, HasData={result.Data != null}, HasDelivery={result.Data?.Delivery != null}",
            $"ErrorMessage: {result.ErrorMessage ?? "none"}, TrackingNumber: {result.Data?.Delivery?.TrackingNumber ?? "null"}, OrderRef: {result.Data?.Delivery?.OrderReference ?? "null"}");

        if (result.Data?.Delivery is null)
        {
            await _log.Error(email.EmailMessageId, "DeliveryParser", "Delivery parser returned no data — flagging for review");
            await FlagForReview(email, "Delivery parser returned no data", ct);
            return null;
        }

        if (result.NeedsReview)
        {
            await FlagForReview(email, $"Low confidence: {result.Confidence}", ct);
            return null;
        }

        var parsed = result.Data.Delivery;

        // Find the shipment by tracking number, or find/create the order by reference
        Shipment? shipment = null;
        Order? order = null;

        if (!string.IsNullOrEmpty(parsed.TrackingNumber))
        {
            shipment = await _db.Shipments
                .IgnoreQueryFilters()
                .Include(s => s.Order)
                .FirstOrDefaultAsync(s => s.TrackingNumber == parsed.TrackingNumber, ct);
            order = shipment?.Order;
        }

        if (order is null)
        {
            order = await FindOrCreateOrderByReference(parsed.OrderReference, email, retailerContext, ct);
            shipment ??= await _db.Shipments
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.OrderId == order.OrderId, ct);
        }

        // If no shipment exists yet, create one from the delivery info
        if (shipment is null)
        {
            shipment = new Shipment
            {
                ShipmentId = Guid.NewGuid(),
                TenantId = email.TenantId,
                OrderId = order.OrderId,
                TrackingNumber = parsed.TrackingNumber,
                Status = ShipmentStatus.Delivered,
                SourceEmailId = email.EmailMessageId
            };
            _db.Shipments.Add(shipment);
            await _db.SaveChangesAsync(ct);
        }

        var deliveryStatus = ParseDeliveryStatus(parsed.Status);
        var issueType = ParseDeliveryIssueType(parsed.IssueType);

        // Check for existing delivery on this shipment
        var delivery = await _db.Deliveries
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.ShipmentId == shipment.ShipmentId, ct);

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

        // Find or create the order
        var order = await FindOrCreateOrderByReference(parsed.OrderReference, email, retailerContext, ct);
        if (order is null)
        {
            // Should never happen since FindOrCreate always returns an order, but just in case
            await FlagForReview(email, $"Could not create order for return: {parsed.OrderReference}", ct);
            return null;
        }

        // Check for existing return by RMA number or order
        Return? returnEntity = null;
        if (!string.IsNullOrEmpty(parsed.RmaNumber))
        {
            returnEntity = await _db.Returns
                .IgnoreQueryFilters()
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

        var order = await FindOrCreateOrderByReference(parsed.OrderReference, email, retailerContext, ct);

        // Find associated return by RMA if available
        Return? associatedReturn = null;
        if (!string.IsNullOrEmpty(parsed.ReturnRma))
        {
            associatedReturn = await _db.Returns
                .IgnoreQueryFilters()
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

        var order = await FindOrCreateOrderByReference(parsed.OrderReference, email, retailerContext, ct);
        if (order is null)
        {
            await FlagForReview(email, $"Could not create order for cancellation: {parsed.OrderReference}", ct);
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

        // Find or create order for payment
        var order = await FindOrCreateOrderByReference(parsed.OrderReference, email, retailerContext, ct);

        order.PaymentMethodSummary = parsed.PaymentMethod;
        order.LastUpdatedEmailId = email.EmailMessageId;

        await CreateTimelineEvent(order.OrderId, email, "PaymentConfirmed",
            $"Payment of {parsed.Amount:C} confirmed",
            $"Method: {parsed.PaymentMethod}", ct);

        await _db.SaveChangesAsync(ct);
        return order.OrderId;
    }

    // ── Helper Methods ──

    private static string NormalizeOrderNumber(string orderRef)
    {
        // Strip leading #, trim whitespace and special chars
        return orderRef.TrimStart('#', ' ').Trim();
    }

    private async Task<Order?> FindOrderByReference(string? orderReference, Guid tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderReference))
            return null;

        var normalized = NormalizeOrderNumber(orderReference);

        // Try exact match first, then normalized, then contains — all bypass RLS
        var order = await _db.Orders
            .IgnoreQueryFilters()
            .Include(o => o.Lines)
            .Where(o => o.TenantId == tenantId)
            .FirstOrDefaultAsync(o => o.ExternalOrderNumber == orderReference
                || o.ExternalOrderNumber == normalized, ct);

        if (order is not null) return order;

        // Try contains match (for partial order numbers)
        if (normalized.Length >= 5) // Avoid overly broad matches
        {
            order = await _db.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Lines)
                .Where(o => o.TenantId == tenantId)
                .FirstOrDefaultAsync(o => o.ExternalOrderNumber.Contains(normalized), ct);
        }

        return order;
    }

    /// <summary>
    /// Finds an existing order by reference, or creates a stub order if none exists.
    /// This ensures shipment, delivery, return, and refund emails always have an order to attach to.
    /// </summary>
    private async Task<Order> FindOrCreateOrderByReference(
        string? orderReference, EmailMessage email, string? retailerName, CancellationToken ct)
    {
        var existing = await FindOrderByReference(orderReference, email.TenantId, ct);
        if (existing is not null) return existing;

        // Create a stub order
        var retailer = await _retailerMatcher.MatchAsync(email.FromAddress, email.OriginalFromAddress, ct);
        var normalized = !string.IsNullOrWhiteSpace(orderReference) ? NormalizeOrderNumber(orderReference) : null;

        var order = new Order
        {
            OrderId = Guid.NewGuid(),
            TenantId = email.TenantId,
            RetailerId = retailer?.RetailerId,
            ExternalOrderNumber = normalized ?? $"UNKNOWN-{email.EmailMessageId:N}",
            OrderDate = email.ReceivedAt,
            Status = OrderStatus.Placed,
            Currency = "USD",
            SourceEmailId = email.EmailMessageId,
            LastUpdatedEmailId = email.EmailMessageId
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        await _log.Info(email.EmailMessageId, "OrderStubCreated",
            $"Created stub order {order.OrderId} for ref '{orderReference}'");

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

    /// <summary>
    /// Adds line items from parsed data that don't already exist on the order.
    /// Matches by product name (case-insensitive).
    /// </summary>
    private void AddNewLineItems(Order order, List<OrderLineData>? lines)
    {
        if (lines is null || lines.Count == 0) return;

        var nextLineNum = (order.Lines?.Any() == true ? order.Lines.Max(l => l.LineNumber) : 0) + 1;
        foreach (var line in lines)
        {
            // Skip if a line with this product name already exists
            var exists = order.Lines?.Any(l =>
                l.ProductName.Contains(line.ProductName, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (exists) continue;

            order.Lines ??= new List<OrderLine>();
            order.Lines.Add(new OrderLine
            {
                OrderLineId = Guid.NewGuid(),
                OrderId = order.OrderId,
                LineNumber = nextLineNum++,
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
