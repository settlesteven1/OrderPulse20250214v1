using System.Text.Json;
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
                $"Source: {bodySource}, RawLength: {body.Length} chars");

            // Pre-process forwarded emails: strip forwarding headers and extract the original body
            if (ForwardedEmailHelper.IsForwardedSubject(email.Subject) || body.Length > 20_000)
            {
                var originalLength = body.Length;
                body = ForwardedEmailHelper.ExtractOriginalBody(body);
                if (body.Length < originalLength)
                {
                    var preview = body.Length > 500 ? body[..500] : body;
                    await _log.Info(emailMessageId, "ForwardStrip",
                        $"Stripped: {originalLength} → {body.Length} chars",
                        $"First 500 chars: {preview}");
                }
            }

            // Route to the appropriate parser
            var orderIds = await RouteAndProcessAsync(email, body, retailerContext, ct);

            if (orderIds.Count > 0)
            {
                foreach (var oid in orderIds)
                {
                    await _log.Success(emailMessageId, "OrderCreated", $"Order ID: {oid}");
                    // Reconcile orphaned shipment/return lines before recalculating status,
                    // since the state machine uses ShipmentLines to compute order status
                    await ReconcileOrderAsync(oid, emailMessageId, ct);
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

        // Detailed diagnostic logging for parser response
        var dataNull = result.Data is null;
        var orderNull = result.Data?.Order is null;
        var linesCount = result.Data?.Lines?.Count ?? 0;
        var ordersCount = result.Data?.Orders?.Count ?? 0;
        var orderNum = result.Data?.Order?.ExternalOrderNumber ?? "(null)";
        await _log.Info(email.EmailMessageId, "OrderParser",
            $"AI response: Data={!dataNull}, Order={!orderNull}, Lines={linesCount}, Orders={ordersCount}, OrderNum={orderNum}, Confidence={result.Confidence}",
            $"ErrorMessage: {result.ErrorMessage ?? "none"}, NeedsReview: {result.NeedsReview}");

        if (result.Data is null)
        {
            await _log.Error(email.EmailMessageId, "OrderParser", "Order parser returned null data — flagging for review");
            await FlagForReview(email, "Order parser returned no data", ct);
            return new List<Guid>();
        }

        // Normalize into a list of orders (handles both single and multi-order emails)
        var allOrders = result.Data.GetAllOrders();

        await _log.Info(email.EmailMessageId, "OrderParser",
            $"Normalized: {allOrders.Count} order(s), Lines per order: [{string.Join(", ", allOrders.Select(o => o.Lines?.Count ?? 0))}]",
            $"OrderNumbers: {string.Join(", ", allOrders.Select(o => o.Order.ExternalOrderNumber ?? "null"))}");

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
            // If this was a stub order created by a shipment/return before the order
            // confirmation arrived, enrich it with the full order data
            if (existingOrder.IsInferred)
            {
                UpdateOrderFromParsed(existingOrder, parsed);
                existingOrder.IsInferred = false;
                existingOrder.OrderDate = TryParseDate(parsed.OrderDate) ?? existingOrder.OrderDate;
                existingOrder.ExternalOrderUrl = parsed.ExternalOrderUrl ?? existingOrder.ExternalOrderUrl;
                await _log.Info(email.EmailMessageId, "StubEnriched",
                    $"Enriched stub order {existingOrder.OrderId} — #{existingOrder.ExternalOrderNumber}");
            }

            // Link email and add any new line items not already on the order
            existingOrder.LastUpdatedEmailId = email.EmailMessageId;
            var hadLines = existingOrder.Lines?.Count > 0;
            AddNewLineItems(existingOrder, lines);
            await _db.SaveChangesAsync(ct);

            // If this enrichment added lines to a stub, retroactively link orphaned shipments/returns
            if (!hadLines && existingOrder.Lines?.Count > 0)
            {
                await LinkOrphanedRecordsToLinesAsync(existingOrder, email, ct);
            }

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
        if (lines.Count == 0)
        {
            await _log.Warn(email.EmailMessageId, "OrderLines",
                $"Parser returned 0 line items for order #{parsed.ExternalOrderNumber} — order will have no lines");
        }

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

        if (lines.Count == 0)
        {
            await _log.Warn(email.EmailMessageId, "OrderCreated",
                $"Order {order.OrderId} created with 0 line items — possible parsing issue");
        }

        await _log.Info(email.EmailMessageId, "OrderCreated",
            $"Created order {order.OrderId} — #{order.ExternalOrderNumber} with {order.Lines.Count} lines",
            lines.Count > 0 ? $"Products: {string.Join("; ", lines.Select(l => $"{l.ProductName} x{l.Quantity}"))}" : "NO LINE ITEMS");

        return order.OrderId;
    }

    // ── Shipment Confirmation / Update ──

    private async Task<Guid?> ProcessShipmentAsync(
        EmailMessage email, string body, string? retailerContext, CancellationToken ct)
    {
        await _log.Info(email.EmailMessageId, "ShipmentParser", $"Calling shipment parser with {body.Length} chars body");
        var result = await _shipmentParser.ParseAsync(email.Subject, body, email.FromAddress, retailerContext, ct);

        var shipmentCount = result.Data?.Shipments?.Count ?? 0;
        var itemCounts = result.Data?.Shipments?.Select(s => s.Items?.Count ?? 0).ToList() ?? new List<int>();
        var orderRefs = result.Data?.Shipments?.Select(s => s.OrderReference ?? "(null)").ToList() ?? new List<string>();
        await _log.Info(email.EmailMessageId, "ShipmentParser",
            $"AI response: Shipments={shipmentCount}, ItemsPerShipment=[{string.Join(", ", itemCounts)}], Confidence={result.Confidence}",
            $"OrderRefs: [{string.Join(", ", orderRefs)}], ErrorMessage: {result.ErrorMessage ?? "none"}");

        if (result.Data?.Shipments is null || result.Data.Shipments.Count == 0)
        {
            await _log.Error(email.EmailMessageId, "ShipmentParser", "Shipment parser returned no data — flagging for review");
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
                    SourceEmailId = email.EmailMessageId,
                    ParsedItemsJson = shipmentData.Items.Count > 0
                        ? JsonSerializer.Serialize(shipmentData.Items)
                        : null
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
                        orderLine.Status = OrderLineStatus.Shipped;
                        orderLine.UpdatedAt = DateTime.UtcNow;
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
            $"AI response: HasData={result.Data != null}, HasDelivery={result.Data?.Delivery != null}, Confidence={result.Confidence}",
            $"OrderRef: {result.Data?.Delivery?.OrderReference ?? "(null)"}, Tracking: {result.Data?.Delivery?.TrackingNumber ?? "(null)"}, " +
            $"Status: {result.Data?.Delivery?.Status ?? "(null)"}, Location: {result.Data?.Delivery?.DeliveryLocation ?? "(null)"}, " +
            $"ErrorMessage: {result.ErrorMessage ?? "none"}");

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

        // Update order line statuses for all lines linked to this shipment
        if (deliveryStatus == DeliveryStatus.Delivered)
        {
            var shipmentLines = await _db.ShipmentLines
                .Where(sl => sl.ShipmentId == shipment.ShipmentId)
                .Include(sl => sl.OrderLine)
                .ToListAsync(ct);
            foreach (var sl in shipmentLines)
            {
                if (sl.OrderLine is not null)
                {
                    sl.OrderLine.Status = OrderLineStatus.Delivered;
                    sl.OrderLine.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

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
                LastUpdatedEmailId = email.EmailMessageId,
                ParsedItemsJson = result.Data.Items.Count > 0
                    ? JsonSerializer.Serialize(result.Data.Items)
                    : null
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
            IsInferred = true,
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

    /// <summary>
    /// Reconciles orphaned ShipmentLines and ReturnLines for an order.
    /// When a shipment/return email processes before the order confirmation, item-to-line
    /// matching fails because the stub order has no OrderLines. This method retries the
    /// matching using the ParsedItemsJson stored on the Shipment/Return entities.
    /// </summary>
    private async Task ReconcileOrderAsync(Guid orderId, Guid emailMessageId, CancellationToken ct)
    {
        var order = await _db.Orders
            .IgnoreQueryFilters()
            .Include(o => o.Lines)
            .Include(o => o.Shipments).ThenInclude(s => s.Lines)
            .Include(o => o.Returns).ThenInclude(r => r.Lines)
            .FirstOrDefaultAsync(o => o.OrderId == orderId, ct);

        if (order is null || order.Lines is null || order.Lines.Count == 0)
            return;

        var reconciled = false;

        // Reconcile shipments with missing ShipmentLines
        foreach (var shipment in order.Shipments)
        {
            if (shipment.Lines.Count > 0 || string.IsNullOrEmpty(shipment.ParsedItemsJson))
                continue;

            List<ShipmentItemData>? parsedItems;
            try
            {
                parsedItems = JsonSerializer.Deserialize<List<ShipmentItemData>>(shipment.ParsedItemsJson);
            }
            catch
            {
                await _log.Warn(emailMessageId, "Reconcile",
                    $"Failed to deserialize ParsedItemsJson for shipment {shipment.ShipmentId}");
                continue;
            }

            if (parsedItems is null || parsedItems.Count == 0) continue;

            var linkedCount = 0;
            foreach (var item in parsedItems)
            {
                var orderLine = order.Lines.FirstOrDefault(l =>
                    l.ProductName.Contains(item.ProductName, StringComparison.OrdinalIgnoreCase));
                if (orderLine is not null)
                {
                    var alreadyLinked = shipment.Lines.Any(sl => sl.OrderLineId == orderLine.OrderLineId);
                    if (!alreadyLinked)
                    {
                        _db.ShipmentLines.Add(new ShipmentLine
                        {
                            ShipmentLineId = Guid.NewGuid(),
                            ShipmentId = shipment.ShipmentId,
                            OrderLineId = orderLine.OrderLineId,
                            Quantity = item.Quantity
                        });
                        linkedCount++;
                    }
                }
            }

            if (linkedCount > 0)
            {
                reconciled = true;
                await _log.Info(emailMessageId, "Reconcile",
                    $"Linked {linkedCount} ShipmentLine(s) for shipment {shipment.ShipmentId}");
            }
        }

        // Reconcile returns with missing ReturnLines
        foreach (var returnEntity in order.Returns)
        {
            if (returnEntity.Lines.Count > 0 || string.IsNullOrEmpty(returnEntity.ParsedItemsJson))
                continue;

            List<ReturnItemData>? parsedItems;
            try
            {
                parsedItems = JsonSerializer.Deserialize<List<ReturnItemData>>(returnEntity.ParsedItemsJson);
            }
            catch
            {
                await _log.Warn(emailMessageId, "Reconcile",
                    $"Failed to deserialize ParsedItemsJson for return {returnEntity.ReturnId}");
                continue;
            }

            if (parsedItems is null || parsedItems.Count == 0) continue;

            var linkedCount = 0;
            foreach (var item in parsedItems)
            {
                var orderLine = order.Lines.FirstOrDefault(l =>
                    l.ProductName.Contains(item.ProductName, StringComparison.OrdinalIgnoreCase));
                if (orderLine is not null)
                {
                    var alreadyLinked = returnEntity.Lines.Any(rl => rl.OrderLineId == orderLine.OrderLineId);
                    if (!alreadyLinked)
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
                        linkedCount++;
                    }
                }
            }

            if (linkedCount > 0)
            {
                reconciled = true;
                await _log.Info(emailMessageId, "Reconcile",
                    $"Linked {linkedCount} ReturnLine(s) for return {returnEntity.ReturnId}");
            }
        }

        if (reconciled)
        {
            await _db.SaveChangesAsync(ct);
            await _log.Success(emailMessageId, "Reconcile",
                $"Reconciliation completed for order {orderId}");
        }
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

    /// <summary>
    /// After a stub order is enriched with line items, retroactively links any existing
    /// Shipment or Return records that were created before the order had lines.
    /// Re-parses the source emails to recover item data for matching.
    /// </summary>
    private async Task LinkOrphanedRecordsToLinesAsync(Order order, EmailMessage triggerEmail, CancellationToken ct)
    {
        var orderId = order.OrderId;
        var tenantId = order.TenantId;

        // Find shipments on this order with zero ShipmentLine records
        var orphanedShipments = await _db.Shipments
            .IgnoreQueryFilters()
            .Include(s => s.Lines)
            .Where(s => s.OrderId == orderId && s.TenantId == tenantId && s.Lines.Count == 0)
            .ToListAsync(ct);

        // Find returns on this order with zero ReturnLine records
        var orphanedReturns = await _db.Returns
            .IgnoreQueryFilters()
            .Include(r => r.Lines)
            .Where(r => r.OrderId == orderId && r.TenantId == tenantId && r.Lines.Count == 0)
            .ToListAsync(ct);

        if (orphanedShipments.Count == 0 && orphanedReturns.Count == 0)
            return;

        await _log.Info(triggerEmail.EmailMessageId, "RetroactiveLink",
            $"Found {orphanedShipments.Count} orphaned shipment(s) and {orphanedReturns.Count} orphaned return(s) for order {orderId}");

        var retailer = await _retailerMatcher.MatchAsync(triggerEmail.FromAddress, ct);
        var retailerContext = retailer?.Name;
        var linkedCount = 0;

        // Retroactively link orphaned shipments
        foreach (var shipment in orphanedShipments)
        {
            var sourceEmail = await _db.EmailMessages
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.EmailMessageId == shipment.SourceEmailId, ct);

            if (sourceEmail is null) continue;

            var body = await FetchEmailBodyAsync(sourceEmail, ct);
            if (string.IsNullOrEmpty(body)) continue;

            var result = await _shipmentParser.ParseAsync(sourceEmail.Subject, body, sourceEmail.FromAddress, retailerContext, ct);
            if (result.Data?.Shipments is null) continue;

            // Match items from all shipments in the parsed result that correspond to this shipment
            // (match by tracking number if available, otherwise use all items)
            var matchingShipmentData = result.Data.Shipments
                .FirstOrDefault(s => !string.IsNullOrEmpty(shipment.TrackingNumber)
                    && s.TrackingNumber == shipment.TrackingNumber)
                ?? result.Data.Shipments.FirstOrDefault();

            if (matchingShipmentData is null) continue;

            foreach (var item in matchingShipmentData.Items)
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
                    linkedCount++;
                }
            }
        }

        // Retroactively link orphaned returns
        foreach (var returnEntity in orphanedReturns)
        {
            var sourceEmail = await _db.EmailMessages
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.EmailMessageId == returnEntity.SourceEmailId, ct);

            if (sourceEmail is null) continue;

            var body = await FetchEmailBodyAsync(sourceEmail, ct);
            if (string.IsNullOrEmpty(body)) continue;

            var result = await _returnParser.ParseAsync(sourceEmail.Subject, body, sourceEmail.FromAddress, retailerContext, ct);
            if (result.Data?.Items is null) continue;

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
                    linkedCount++;
                }
            }
        }

        if (linkedCount > 0)
        {
            await _db.SaveChangesAsync(ct);
            await _log.Info(triggerEmail.EmailMessageId, "RetroactiveLink",
                $"Linked {linkedCount} line(s) to orphaned shipments/returns for order {orderId}");
        }
    }

    /// <summary>
    /// Fetches the full email body from blob storage, falling back to the body preview.
    /// </summary>
    private async Task<string?> FetchEmailBodyAsync(EmailMessage email, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(email.BodyBlobUrl))
        {
            try
            {
                var body = await _blobStorage.GetEmailBodyAsync(email.BodyBlobUrl, ct);
                if (!string.IsNullOrEmpty(body))
                    return body;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch blob for email {id}, falling back to preview", email.EmailMessageId);
            }
        }

        return email.BodyPreview;
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
