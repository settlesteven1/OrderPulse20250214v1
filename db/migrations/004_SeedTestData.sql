-- ============================================================================
-- OrderPulse Test Data
-- Migration 004: Seed realistic test data for development/demo
-- Run AFTER 001, 002, 003
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- First, get the TenantId (assumes you've already inserted a Tenant row)
DECLARE @TenantId UNIQUEIDENTIFIER;
SELECT TOP 1 @TenantId = TenantId FROM [dbo].[Tenants] WHERE IsActive = 1;

IF @TenantId IS NULL
BEGIN
    PRINT 'No active tenant found. Creating one...';
    SET @TenantId = NEWID();
    INSERT INTO [dbo].[Tenants] (TenantId, DisplayName, Email, PurchaseMailbox, MailboxProvider, IsActive)
    VALUES (@TenantId, 'Steven Settle', 'bangupjobasusual@gmail.com', 'bangupjobasusual@gmail.com', 'AzureExchange', 1);
END

PRINT 'Using TenantId: ' + CAST(@TenantId AS NVARCHAR(36));

-- Set SESSION_CONTEXT so Row-Level Security allows our inserts
EXEC sp_set_session_context @key=N'TenantId', @value=@TenantId;

-- Get Retailer IDs
DECLARE @Amazon UNIQUEIDENTIFIER, @Walmart UNIQUEIDENTIFIER, @Target UNIQUEIDENTIFIER,
        @BestBuy UNIQUEIDENTIFIER, @Apple UNIQUEIDENTIFIER, @Nike UNIQUEIDENTIFIER;

SELECT @Amazon = RetailerId FROM Retailers WHERE NormalizedName = 'amazon';
SELECT @Walmart = RetailerId FROM Retailers WHERE NormalizedName = 'walmart';
SELECT @Target = RetailerId FROM Retailers WHERE NormalizedName = 'target';
SELECT @BestBuy = RetailerId FROM Retailers WHERE NormalizedName = 'best buy';
SELECT @Apple = RetailerId FROM Retailers WHERE NormalizedName = 'apple';
SELECT @Nike = RetailerId FROM Retailers WHERE NormalizedName = 'nike';

-- ============================================================================
-- ORDER 1: Amazon - Delivered (complete lifecycle)
-- ============================================================================
DECLARE @Order1 UNIQUEIDENTIFIER = NEWID();
DECLARE @O1L1 UNIQUEIDENTIFIER = NEWID();
DECLARE @O1L2 UNIQUEIDENTIFIER = NEWID();
DECLARE @O1L3 UNIQUEIDENTIFIER = NEWID();
DECLARE @Ship1 UNIQUEIDENTIFIER = NEWID();
DECLARE @Del1 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Orders (OrderId, TenantId, RetailerId, ExternalOrderNumber, OrderDate, Status, Subtotal, TaxAmount, ShippingCost, DiscountAmount, TotalAmount, Currency, EstimatedDeliveryStart, EstimatedDeliveryEnd, ShippingAddress, PaymentMethodSummary)
VALUES (@Order1, @TenantId, @Amazon, '111-2345678-9012345', '2026-02-01', 'Delivered', 89.97, 7.42, 0.00, 5.00, 92.39, 'USD', '2026-02-04', '2026-02-07', '123 Main St, Austin TX 78701', 'Visa ending in 4242');

INSERT INTO OrderLines (OrderLineId, OrderId, LineNumber, ProductName, Quantity, UnitPrice, LineTotal, Status)
VALUES
    (@O1L1, @Order1, 1, 'Anker USB-C Hub 7-in-1', 1, 34.99, 34.99, 'Delivered'),
    (@O1L2, @Order1, 2, 'Samsung EVO 256GB microSD Card', 2, 17.49, 34.98, 'Delivered'),
    (@O1L3, @Order1, 3, 'Cable Matters USB-C to HDMI Adapter', 1, 19.99, 19.99, 'Delivered');

INSERT INTO Shipments (ShipmentId, TenantId, OrderId, Carrier, CarrierNormalized, TrackingNumber, TrackingUrl, ShipDate, EstimatedDelivery, Status, LastStatusUpdate)
VALUES (@Ship1, @TenantId, @Order1, 'Amazon Logistics', 'Amazon', 'TBA349286174000', 'https://www.amazon.com/gp/css/shipment-tracking', '2026-02-02', '2026-02-05', 'Delivered', 'Delivered to front door');

INSERT INTO ShipmentLines (ShipmentId, OrderLineId, Quantity)
VALUES (@Ship1, @O1L1, 1), (@Ship1, @O1L2, 2), (@Ship1, @O1L3, 1);

INSERT INTO Deliveries (DeliveryId, TenantId, ShipmentId, DeliveryDate, DeliveryLocation, Status)
VALUES (@Del1, @TenantId, @Ship1, '2026-02-05 14:23:00', 'Front door', 'Delivered');

INSERT INTO OrderEvents (TenantId, OrderId, EventType, EventDate, Summary, EntityType, EntityId)
VALUES
    (@TenantId, @Order1, 'OrderPlaced', '2026-02-01 09:15:00', 'Order placed on Amazon for $92.39', 'Order', @Order1),
    (@TenantId, @Order1, 'Shipped', '2026-02-02 16:30:00', 'Shipped via Amazon Logistics - TBA349286174000', 'Shipment', @Ship1),
    (@TenantId, @Order1, 'Delivered', '2026-02-05 14:23:00', 'Delivered to front door', 'Delivery', @Del1);

-- ============================================================================
-- ORDER 2: Best Buy - In Transit (awaiting delivery)
-- ============================================================================
DECLARE @Order2 UNIQUEIDENTIFIER = NEWID();
DECLARE @O2L1 UNIQUEIDENTIFIER = NEWID();
DECLARE @Ship2 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Orders (OrderId, TenantId, RetailerId, ExternalOrderNumber, OrderDate, Status, Subtotal, TaxAmount, ShippingCost, TotalAmount, Currency, EstimatedDeliveryStart, EstimatedDeliveryEnd, ShippingAddress, PaymentMethodSummary)
VALUES (@Order2, @TenantId, @BestBuy, 'BBY01-806547321', '2026-02-10', 'InTransit', 1299.99, 107.25, 0.00, 1407.24, 'USD', '2026-02-15', '2026-02-18', '123 Main St, Austin TX 78701', 'Amex ending in 1001');

INSERT INTO OrderLines (OrderLineId, OrderId, LineNumber, ProductName, Quantity, UnitPrice, LineTotal, Status)
VALUES (@O2L1, @Order2, 1, 'Sony WH-1000XM5 Wireless Headphones', 1, 1299.99, 1299.99, 'Shipped');

INSERT INTO Shipments (ShipmentId, TenantId, OrderId, Carrier, CarrierNormalized, TrackingNumber, TrackingUrl, ShipDate, EstimatedDelivery, Status, LastStatusUpdate, LastStatusDate)
VALUES (@Ship2, @TenantId, @Order2, 'UPS', 'UPS', '1Z999AA10123456784', 'https://www.ups.com/track?tracknum=1Z999AA10123456784', '2026-02-12', '2026-02-17', 'InTransit', 'In transit - Memphis, TN', '2026-02-15 08:00:00');

INSERT INTO ShipmentLines (ShipmentId, OrderLineId, Quantity) VALUES (@Ship2, @O2L1, 1);

INSERT INTO OrderEvents (TenantId, OrderId, EventType, EventDate, Summary, EntityType, EntityId)
VALUES
    (@TenantId, @Order2, 'OrderPlaced', '2026-02-10 11:00:00', 'Order placed on Best Buy for $1,407.24', 'Order', @Order2),
    (@TenantId, @Order2, 'Shipped', '2026-02-12 09:45:00', 'Shipped via UPS - 1Z999AA10123456784', 'Shipment', @Ship2),
    (@TenantId, @Order2, 'StatusUpdate', '2026-02-15 08:00:00', 'In transit - Memphis, TN', 'Shipment', @Ship2);

-- ============================================================================
-- ORDER 3: Target - Partially Shipped (split shipment)
-- ============================================================================
DECLARE @Order3 UNIQUEIDENTIFIER = NEWID();
DECLARE @O3L1 UNIQUEIDENTIFIER = NEWID();
DECLARE @O3L2 UNIQUEIDENTIFIER = NEWID();
DECLARE @Ship3 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Orders (OrderId, TenantId, RetailerId, ExternalOrderNumber, OrderDate, Status, Subtotal, TaxAmount, ShippingCost, TotalAmount, Currency, EstimatedDeliveryStart, EstimatedDeliveryEnd, ShippingAddress, PaymentMethodSummary)
VALUES (@Order3, @TenantId, @Target, '102-9384756-1234', '2026-02-08', 'PartiallyShipped', 67.98, 5.61, 0.00, 73.59, 'USD', '2026-02-12', '2026-02-16', '123 Main St, Austin TX 78701', 'Target RedCard ending in 8899');

INSERT INTO OrderLines (OrderLineId, OrderId, LineNumber, ProductName, Quantity, UnitPrice, LineTotal, Status)
VALUES
    (@O3L1, @Order3, 1, 'Threshold Decorative Throw Pillow - Sage Green', 2, 18.99, 37.98, 'Shipped'),
    (@O3L2, @Order3, 2, 'Casaluna Linen Sheet Set - Queen', 1, 30.00, 30.00, 'Ordered');

INSERT INTO Shipments (ShipmentId, TenantId, OrderId, Carrier, CarrierNormalized, TrackingNumber, TrackingUrl, ShipDate, EstimatedDelivery, Status, LastStatusUpdate)
VALUES (@Ship3, @TenantId, @Order3, 'USPS', 'USPS', '9400111899223100012345', 'https://tools.usps.com/go/TrackConfirmAction?tLabels=9400111899223100012345', '2026-02-10', '2026-02-14', 'InTransit', 'In transit to destination');

INSERT INTO ShipmentLines (ShipmentId, OrderLineId, Quantity) VALUES (@Ship3, @O3L1, 2);

INSERT INTO OrderEvents (TenantId, OrderId, EventType, EventDate, Summary, EntityType, EntityId)
VALUES
    (@TenantId, @Order3, 'OrderPlaced', '2026-02-08 19:30:00', 'Order placed on Target for $73.59', 'Order', @Order3),
    (@TenantId, @Order3, 'PartiallyShipped', '2026-02-10 12:00:00', 'Throw pillows shipped via USPS. Sheet set still processing.', 'Shipment', @Ship3);

-- ============================================================================
-- ORDER 4: Nike - Return In Progress (need to ship back)
-- ============================================================================
DECLARE @Order4 UNIQUEIDENTIFIER = NEWID();
DECLARE @O4L1 UNIQUEIDENTIFIER = NEWID();
DECLARE @O4L2 UNIQUEIDENTIFIER = NEWID();
DECLARE @Ship4 UNIQUEIDENTIFIER = NEWID();
DECLARE @Del4 UNIQUEIDENTIFIER = NEWID();
DECLARE @Ret4 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Orders (OrderId, TenantId, RetailerId, ExternalOrderNumber, OrderDate, Status, Subtotal, TaxAmount, ShippingCost, TotalAmount, Currency, ShippingAddress, PaymentMethodSummary)
VALUES (@Order4, @TenantId, @Nike, 'C01234567890', '2026-01-20', 'ReturnInProgress', 184.98, 15.26, 0.00, 200.24, 'USD', '123 Main St, Austin TX 78701', 'PayPal');

INSERT INTO OrderLines (OrderLineId, OrderId, LineNumber, ProductName, Quantity, UnitPrice, LineTotal, Status)
VALUES
    (@O4L1, @Order4, 1, 'Nike Air Max 270 - Black/White Size 11', 1, 149.99, 149.99, 'ReturnInitiated'),
    (@O4L2, @Order4, 2, 'Nike Dri-FIT Running Socks 6-Pack', 1, 34.99, 34.99, 'Delivered');

INSERT INTO Shipments (ShipmentId, TenantId, OrderId, Carrier, CarrierNormalized, TrackingNumber, ShipDate, Status)
VALUES (@Ship4, @TenantId, @Order4, 'FedEx', 'FedEx', '794644790138', '2026-01-21', 'Delivered');
INSERT INTO ShipmentLines (ShipmentId, OrderLineId, Quantity) VALUES (@Ship4, @O4L1, 1), (@Ship4, @O4L2, 1);
INSERT INTO Deliveries (DeliveryId, TenantId, ShipmentId, DeliveryDate, DeliveryLocation, Status)
VALUES (@Del4, @TenantId, @Ship4, '2026-01-24 11:30:00', 'Front porch', 'Delivered');

INSERT INTO Returns (ReturnId, TenantId, OrderId, RMANumber, Status, ReturnReason, ReturnMethod, ReturnCarrier, ReturnLabelBlobUrl, QRCodeData, DropOffLocation, DropOffAddress, ReturnByDate)
VALUES (@Ret4, @TenantId, @Order4, 'NKE-RMA-88421', 'LabelIssued', 'Wrong size - need size 10.5', 'DropOff', 'UPS', 'https://labels.nike.com/return/88421.pdf', 'NIKERETURN:88421:UPS', 'UPS Store #4521', '456 Commerce Dr, Austin TX 78745', '2026-03-20');

INSERT INTO ReturnLines (ReturnId, OrderLineId, Quantity, ReturnReason)
VALUES (@Ret4, @O4L1, 1, 'Wrong size - need size 10.5');

INSERT INTO OrderEvents (TenantId, OrderId, EventType, EventDate, Summary, EntityType, EntityId)
VALUES
    (@TenantId, @Order4, 'OrderPlaced', '2026-01-20 15:00:00', 'Order placed on Nike for $200.24', 'Order', @Order4),
    (@TenantId, @Order4, 'Shipped', '2026-01-21 10:00:00', 'Shipped via FedEx', 'Shipment', @Ship4),
    (@TenantId, @Order4, 'Delivered', '2026-01-24 11:30:00', 'Delivered to front porch', 'Delivery', @Del4),
    (@TenantId, @Order4, 'ReturnInitiated', '2026-02-01 09:00:00', 'Return initiated for Nike Air Max 270 - wrong size', 'Return', @Ret4),
    (@TenantId, @Order4, 'ReturnLabelIssued', '2026-02-01 09:05:00', 'Return label issued - drop off at UPS Store #4521', 'Return', @Ret4);

-- ============================================================================
-- ORDER 5: Apple - Awaiting Refund
-- ============================================================================
DECLARE @Order5 UNIQUEIDENTIFIER = NEWID();
DECLARE @O5L1 UNIQUEIDENTIFIER = NEWID();
DECLARE @Ship5 UNIQUEIDENTIFIER = NEWID();
DECLARE @Del5 UNIQUEIDENTIFIER = NEWID();
DECLARE @Ret5 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Orders (OrderId, TenantId, RetailerId, ExternalOrderNumber, OrderDate, Status, Subtotal, TaxAmount, ShippingCost, TotalAmount, Currency, ShippingAddress, PaymentMethodSummary)
VALUES (@Order5, @TenantId, @Apple, 'W1234567890', '2026-01-10', 'ReturnReceived', 199.00, 16.42, 0.00, 215.42, 'USD', '123 Main St, Austin TX 78701', 'Apple Pay');

INSERT INTO OrderLines (OrderLineId, OrderId, LineNumber, ProductName, Quantity, UnitPrice, LineTotal, Status)
VALUES (@O5L1, @Order5, 1, 'AirPods Pro 2nd Gen', 1, 199.00, 199.00, 'Returned');

INSERT INTO Shipments (ShipmentId, TenantId, OrderId, Carrier, CarrierNormalized, TrackingNumber, ShipDate, Status)
VALUES (@Ship5, @TenantId, @Order5, 'UPS', 'UPS', '1Z999BB20234567891', '2026-01-11', 'Delivered');
INSERT INTO ShipmentLines (ShipmentId, OrderLineId, Quantity) VALUES (@Ship5, @O5L1, 1);
INSERT INTO Deliveries (DeliveryId, TenantId, ShipmentId, DeliveryDate, Status)
VALUES (@Del5, @TenantId, @Ship5, '2026-01-14 10:00:00', 'Delivered');

INSERT INTO Returns (ReturnId, TenantId, OrderId, RMANumber, Status, ReturnReason, ReturnMethod, ReturnCarrier, ReturnTrackingNumber, ReturnByDate, ReceivedByRetailerDate)
VALUES (@Ret5, @TenantId, @Order5, 'APL-R-90123', 'Received', 'Defective - left earbud crackling', 'Mail', 'UPS', '1Z999CC30345678902', '2026-01-24', '2026-02-08');

INSERT INTO ReturnLines (ReturnId, OrderLineId, Quantity, ReturnReason)
VALUES (@Ret5, @O5L1, 1, 'Defective - left earbud crackling');

INSERT INTO OrderEvents (TenantId, OrderId, EventType, EventDate, Summary, EntityType, EntityId)
VALUES
    (@TenantId, @Order5, 'OrderPlaced', '2026-01-10 10:00:00', 'Order placed on Apple for $215.42', 'Order', @Order5),
    (@TenantId, @Order5, 'Delivered', '2026-01-14 10:00:00', 'Delivered', 'Delivery', @Del5),
    (@TenantId, @Order5, 'ReturnInitiated', '2026-01-18 14:00:00', 'Return initiated - defective AirPods Pro', 'Return', @Ret5),
    (@TenantId, @Order5, 'ReturnShipped', '2026-01-20 09:00:00', 'Return shipped via UPS', 'Return', @Ret5),
    (@TenantId, @Order5, 'ReturnReceived', '2026-02-08 12:00:00', 'Return received by Apple. Refund pending.', 'Return', @Ret5);

-- ============================================================================
-- ORDER 6: Walmart - Cancelled
-- ============================================================================
DECLARE @Order6 UNIQUEIDENTIFIER = NEWID();
DECLARE @O6L1 UNIQUEIDENTIFIER = NEWID();
DECLARE @Refund6 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Orders (OrderId, TenantId, RetailerId, ExternalOrderNumber, OrderDate, Status, Subtotal, TaxAmount, ShippingCost, TotalAmount, Currency, PaymentMethodSummary)
VALUES (@Order6, @TenantId, @Walmart, '2000123-456789', '2026-02-05', 'Cancelled', 249.99, 20.62, 0.00, 270.61, 'USD', 'Debit card ending in 5577');

INSERT INTO OrderLines (OrderLineId, OrderId, LineNumber, ProductName, Quantity, UnitPrice, LineTotal, Status)
VALUES (@O6L1, @Order6, 1, 'Dyson V8 Cordless Vacuum', 1, 249.99, 249.99, 'Cancelled');

INSERT INTO Refunds (RefundId, TenantId, OrderId, RefundAmount, Currency, RefundMethod, RefundDate, TransactionId)
VALUES (@Refund6, @TenantId, @Order6, 270.61, 'USD', 'Original payment method', '2026-02-06 08:00:00', 'WMT-REF-998877');

INSERT INTO OrderEvents (TenantId, OrderId, EventType, EventDate, Summary, EntityType, EntityId)
VALUES
    (@TenantId, @Order6, 'OrderPlaced', '2026-02-05 20:15:00', 'Order placed on Walmart for $270.61', 'Order', @Order6),
    (@TenantId, @Order6, 'OrderCancelled', '2026-02-06 07:30:00', 'Order cancelled by customer', 'Order', @Order6),
    (@TenantId, @Order6, 'RefundIssued', '2026-02-06 08:00:00', 'Refund of $270.61 issued to original payment method', 'Refund', @Refund6);

-- ============================================================================
-- ORDER 7: Amazon - Delivery Exception (needs attention)
-- ============================================================================
DECLARE @Order7 UNIQUEIDENTIFIER = NEWID();
DECLARE @O7L1 UNIQUEIDENTIFIER = NEWID();
DECLARE @Ship7 UNIQUEIDENTIFIER = NEWID();
DECLARE @Del7 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Orders (OrderId, TenantId, RetailerId, ExternalOrderNumber, OrderDate, Status, Subtotal, TaxAmount, ShippingCost, TotalAmount, Currency, EstimatedDeliveryEnd, ShippingAddress, PaymentMethodSummary)
VALUES (@Order7, @TenantId, @Amazon, '111-8765432-1098765', '2026-02-09', 'DeliveryException', 45.99, 3.79, 5.99, 55.77, 'USD', '2026-02-13', '123 Main St, Austin TX 78701', 'Visa ending in 4242');

INSERT INTO OrderLines (OrderLineId, OrderId, LineNumber, ProductName, Quantity, UnitPrice, LineTotal, Status)
VALUES (@O7L1, @Order7, 1, 'Logitech MX Keys Mini Keyboard', 1, 45.99, 45.99, 'Shipped');

INSERT INTO Shipments (ShipmentId, TenantId, OrderId, Carrier, CarrierNormalized, TrackingNumber, TrackingUrl, ShipDate, EstimatedDelivery, Status, LastStatusUpdate, LastStatusDate)
VALUES (@Ship7, @TenantId, @Order7, 'USPS', 'USPS', '9261290100130435082878', 'https://tools.usps.com/go/TrackConfirmAction?tLabels=9261290100130435082878', '2026-02-11', '2026-02-14', 'Exception', 'Delivery attempted - no secure location available', '2026-02-14 17:45:00');

INSERT INTO ShipmentLines (ShipmentId, OrderLineId, Quantity) VALUES (@Ship7, @O7L1, 1);

INSERT INTO Deliveries (DeliveryId, TenantId, ShipmentId, DeliveryDate, Status, IssueType, IssueDescription)
VALUES (@Del7, @TenantId, @Ship7, '2026-02-14 17:45:00', 'AttemptedDelivery', 'NotReceived', 'Delivery attempted but no secure location. Will retry next business day.');

INSERT INTO OrderEvents (TenantId, OrderId, EventType, EventDate, Summary, EntityType, EntityId)
VALUES
    (@TenantId, @Order7, 'OrderPlaced', '2026-02-09 13:00:00', 'Order placed on Amazon for $55.77', 'Order', @Order7),
    (@TenantId, @Order7, 'Shipped', '2026-02-11 08:00:00', 'Shipped via USPS', 'Shipment', @Ship7),
    (@TenantId, @Order7, 'DeliveryException', '2026-02-14 17:45:00', 'Delivery attempted - no secure location available. Will retry.', 'Delivery', @Del7);

-- ============================================================================
-- ORDER 8: Amazon - Placed today (just ordered)
-- ============================================================================
DECLARE @Order8 UNIQUEIDENTIFIER = NEWID();
DECLARE @O8L1 UNIQUEIDENTIFIER = NEWID();
DECLARE @O8L2 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Orders (OrderId, TenantId, RetailerId, ExternalOrderNumber, OrderDate, Status, Subtotal, TaxAmount, ShippingCost, TotalAmount, Currency, EstimatedDeliveryStart, EstimatedDeliveryEnd, ShippingAddress, PaymentMethodSummary)
VALUES (@Order8, @TenantId, @Amazon, '111-5555555-6666666', GETUTCDATE(), 'Placed', 54.97, 4.54, 0.00, 59.51, 'USD', DATEADD(DAY, 3, GETUTCDATE()), DATEADD(DAY, 5, GETUTCDATE()), '123 Main St, Austin TX 78701', 'Visa ending in 4242');

INSERT INTO OrderLines (OrderLineId, OrderId, LineNumber, ProductName, Quantity, UnitPrice, LineTotal, Status)
VALUES
    (@O8L1, @Order8, 1, 'Bose QuietComfort Earbuds II', 1, 29.99, 29.99, 'Ordered'),
    (@O8L2, @Order8, 2, 'Anker Nano Charger 20W', 1, 24.98, 24.98, 'Ordered');

INSERT INTO OrderEvents (TenantId, OrderId, EventType, EventDate, Summary, EntityType, EntityId)
VALUES (@TenantId, @Order8, 'OrderPlaced', GETUTCDATE(), 'Order placed on Amazon for $59.51', 'Order', @Order8);

-- ============================================================================
-- SUMMARY
-- ============================================================================
PRINT '';
PRINT '=== Test Data Loaded ===';
PRINT 'Order 1: Amazon - Delivered (USB hub + accessories)';
PRINT 'Order 2: Best Buy - In Transit (Sony headphones)';
PRINT 'Order 3: Target - Partially Shipped (home goods)';
PRINT 'Order 4: Nike - Return In Progress (wrong size shoes)';
PRINT 'Order 5: Apple - Awaiting Refund (defective AirPods)';
PRINT 'Order 6: Walmart - Cancelled + Refunded (vacuum)';
PRINT 'Order 7: Amazon - Delivery Exception (keyboard)';
PRINT 'Order 8: Amazon - Just Placed (earbuds + charger)';
PRINT '';
SELECT COUNT(*) AS TotalOrders FROM Orders WHERE TenantId = @TenantId;
SELECT COUNT(*) AS TotalOrderLines FROM OrderLines OL INNER JOIN Orders O ON OL.OrderId = O.OrderId WHERE O.TenantId = @TenantId;
SELECT COUNT(*) AS TotalShipments FROM Shipments WHERE TenantId = @TenantId;
SELECT COUNT(*) AS TotalEvents FROM OrderEvents WHERE TenantId = @TenantId;
GO
