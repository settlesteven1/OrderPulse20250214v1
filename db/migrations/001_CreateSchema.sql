-- ============================================================================
-- OrderPulse Database Schema
-- Migration 001: Create all tables, indexes, and constraints
-- Target: Azure SQL Database
-- ============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================================
-- 1. TENANTS
-- ============================================================================
CREATE TABLE [dbo].[Tenants] (
    [TenantId]              UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [DisplayName]           NVARCHAR(200)       NOT NULL,
    [Email]                 NVARCHAR(320)       NOT NULL,
    [PurchaseMailbox]       NVARCHAR(320)       NOT NULL,
    [MailboxProvider]       NVARCHAR(50)        NOT NULL,  -- AzureExchange | Gmail | Other
    [GraphRefreshToken]     NVARCHAR(MAX)       NULL,      -- Encrypted OAuth refresh token
    [GraphSubscriptionId]   NVARCHAR(200)       NULL,      -- MS Graph webhook subscription ID
    [IsActive]              BIT                 NOT NULL DEFAULT 1,
    [CreatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),
    [LastSyncAt]            DATETIME2           NULL,

    CONSTRAINT [PK_Tenants] PRIMARY KEY CLUSTERED ([TenantId]),
    CONSTRAINT [UQ_Tenants_Email] UNIQUE ([Email]),
    CONSTRAINT [CK_Tenants_MailboxProvider] CHECK ([MailboxProvider] IN ('AzureExchange', 'Gmail', 'Other'))
);
GO

-- ============================================================================
-- 2. RETAILERS
-- ============================================================================
CREATE TABLE [dbo].[Retailers] (
    [RetailerId]            UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [Name]                  NVARCHAR(200)       NOT NULL,
    [NormalizedName]        NVARCHAR(200)       NOT NULL,  -- Lowercase, trimmed for matching
    [SenderDomains]         NVARCHAR(MAX)       NOT NULL,  -- JSON array: ["amazon.com","amazon.co.uk"]
    [SenderPatterns]        NVARCHAR(MAX)       NULL,      -- JSON array of regex patterns for sender matching
    [LogoUrl]               NVARCHAR(2000)      NULL,
    [WebsiteUrl]            NVARCHAR(2000)      NULL,
    [ReturnPolicyDays]      INT                 NULL,
    [ReturnPolicyNotes]     NVARCHAR(1000)      NULL,
    [CreatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_Retailers] PRIMARY KEY CLUSTERED ([RetailerId])
);
GO

CREATE NONCLUSTERED INDEX [IX_Retailers_NormalizedName]
    ON [dbo].[Retailers] ([NormalizedName]);
GO

-- ============================================================================
-- 3. EMAIL MESSAGES
-- ============================================================================
CREATE TABLE [dbo].[EmailMessages] (
    [EmailMessageId]            UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [TenantId]                  UNIQUEIDENTIFIER    NOT NULL,
    [GraphMessageId]            NVARCHAR(500)       NOT NULL,  -- MS Graph message ID for dedup
    [InternetMessageId]         NVARCHAR(500)       NULL,      -- RFC 2822 Message-ID header
    [FromAddress]               NVARCHAR(320)       NOT NULL,
    [FromDisplayName]           NVARCHAR(200)       NULL,
    [Subject]                   NVARCHAR(1000)      NOT NULL,
    [ReceivedAt]                DATETIME2           NOT NULL,
    [BodyBlobUrl]               NVARCHAR(2000)      NOT NULL,  -- Azure Blob Storage URL
    [BodyPreview]               NVARCHAR(1000)      NULL,      -- First ~500 chars for quick display
    [HasAttachments]            BIT                 NOT NULL DEFAULT 0,
    [ClassificationType]        NVARCHAR(50)        NULL,      -- See CHECK constraint
    [ClassificationConfidence]  DECIMAL(3,2)        NULL,
    [ProcessingStatus]          NVARCHAR(30)        NOT NULL DEFAULT 'Pending',
    [ProcessedAt]               DATETIME2           NULL,
    [ErrorDetails]              NVARCHAR(MAX)       NULL,
    [RetryCount]                INT                 NOT NULL DEFAULT 0,
    [CreatedAt]                 DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_EmailMessages] PRIMARY KEY CLUSTERED ([EmailMessageId]),
    CONSTRAINT [FK_EmailMessages_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [CK_EmailMessages_ProcessingStatus] CHECK ([ProcessingStatus] IN (
        'Pending', 'Classifying', 'Classified', 'Parsing', 'Parsed', 'Failed', 'ManualReview', 'Dismissed'
    )),
    CONSTRAINT [CK_EmailMessages_ClassificationType] CHECK ([ClassificationType] IS NULL OR [ClassificationType] IN (
        'OrderConfirmation', 'OrderModification', 'OrderCancellation', 'PaymentConfirmation',
        'ShipmentConfirmation', 'ShipmentUpdate', 'DeliveryConfirmation', 'DeliveryIssue',
        'ReturnInitiation', 'ReturnLabel', 'ReturnReceived', 'ReturnRejection',
        'RefundConfirmation', 'Promotional'
    ))
);
GO

CREATE NONCLUSTERED INDEX [IX_EmailMessages_TenantId_ProcessingStatus]
    ON [dbo].[EmailMessages] ([TenantId], [ProcessingStatus])
    INCLUDE ([ReceivedAt], [ClassificationType]);
GO

CREATE UNIQUE NONCLUSTERED INDEX [IX_EmailMessages_TenantId_GraphMessageId]
    ON [dbo].[EmailMessages] ([TenantId], [GraphMessageId]);
GO

-- ============================================================================
-- 4. ORDERS (Header)
-- ============================================================================
CREATE TABLE [dbo].[Orders] (
    [OrderId]                   UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [TenantId]                  UNIQUEIDENTIFIER    NOT NULL,
    [RetailerId]                UNIQUEIDENTIFIER    NULL,
    [ExternalOrderNumber]       NVARCHAR(200)       NOT NULL,
    [ExternalOrderUrl]          NVARCHAR(2000)      NULL,      -- Link to retailer's order page
    [OrderDate]                 DATETIME2           NOT NULL,
    [Status]                    NVARCHAR(50)        NOT NULL DEFAULT 'Placed',
    [Subtotal]                  DECIMAL(10,2)       NULL,
    [TaxAmount]                 DECIMAL(10,2)       NULL,
    [ShippingCost]              DECIMAL(10,2)       NULL,
    [DiscountAmount]            DECIMAL(10,2)       NULL,
    [TotalAmount]               DECIMAL(10,2)       NULL,
    [Currency]                  NVARCHAR(3)         NOT NULL DEFAULT 'USD',
    [EstimatedDeliveryStart]    DATE                NULL,
    [EstimatedDeliveryEnd]      DATE                NULL,
    [ShippingAddress]           NVARCHAR(1000)      NULL,
    [PaymentMethodSummary]      NVARCHAR(200)       NULL,      -- e.g. "Visa ending in 4242"
    [IsInferred]                BIT                 NOT NULL DEFAULT 0,  -- True if order was created from a non-order email
    [SourceEmailId]             UNIQUEIDENTIFIER    NULL,
    [LastUpdatedEmailId]        UNIQUEIDENTIFIER    NULL,
    [CreatedAt]                 DATETIME2           NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]                 DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([OrderId]),
    CONSTRAINT [FK_Orders_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [FK_Orders_Retailers] FOREIGN KEY ([RetailerId]) REFERENCES [dbo].[Retailers] ([RetailerId]),
    CONSTRAINT [FK_Orders_SourceEmail] FOREIGN KEY ([SourceEmailId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId]),
    CONSTRAINT [FK_Orders_LastEmail] FOREIGN KEY ([LastUpdatedEmailId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId]),
    CONSTRAINT [CK_Orders_Status] CHECK ([Status] IN (
        'Placed', 'PartiallyShipped', 'Shipped', 'InTransit', 'OutForDelivery',
        'PartiallyDelivered', 'Delivered', 'DeliveryException',
        'ReturnInProgress', 'ReturnReceived', 'Refunded',
        'Cancelled', 'PartiallyCancelled', 'Closed', 'Inferred'
    ))
);
GO

CREATE NONCLUSTERED INDEX [IX_Orders_TenantId_Status]
    ON [dbo].[Orders] ([TenantId], [Status])
    INCLUDE ([OrderDate], [ExternalOrderNumber], [RetailerId], [TotalAmount]);
GO

CREATE NONCLUSTERED INDEX [IX_Orders_TenantId_ExternalOrderNumber]
    ON [dbo].[Orders] ([TenantId], [ExternalOrderNumber])
    INCLUDE ([RetailerId], [Status]);
GO

CREATE NONCLUSTERED INDEX [IX_Orders_TenantId_OrderDate]
    ON [dbo].[Orders] ([TenantId], [OrderDate] DESC)
    INCLUDE ([Status], [ExternalOrderNumber], [RetailerId], [TotalAmount]);
GO

-- ============================================================================
-- 5. ORDER LINES
-- ============================================================================
CREATE TABLE [dbo].[OrderLines] (
    [OrderLineId]       UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [OrderId]           UNIQUEIDENTIFIER    NOT NULL,
    [LineNumber]        INT                 NOT NULL,
    [ProductName]       NVARCHAR(500)       NOT NULL,
    [ProductUrl]        NVARCHAR(2000)      NULL,
    [SKU]               NVARCHAR(100)       NULL,
    [Quantity]          INT                 NOT NULL DEFAULT 1,
    [UnitPrice]         DECIMAL(10,2)       NULL,
    [LineTotal]         DECIMAL(10,2)       NULL,
    [Status]            NVARCHAR(50)        NOT NULL DEFAULT 'Ordered',
    [ImageUrl]          NVARCHAR(2000)      NULL,
    [CreatedAt]         DATETIME2           NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]         DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_OrderLines] PRIMARY KEY CLUSTERED ([OrderLineId]),
    CONSTRAINT [FK_OrderLines_Orders] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId]) ON DELETE CASCADE,
    CONSTRAINT [UQ_OrderLines_OrderId_LineNumber] UNIQUE ([OrderId], [LineNumber]),
    CONSTRAINT [CK_OrderLines_Status] CHECK ([Status] IN (
        'Ordered', 'Shipped', 'Delivered', 'Cancelled', 'ReturnInitiated', 'Returned', 'Refunded'
    )),
    CONSTRAINT [CK_OrderLines_Quantity] CHECK ([Quantity] > 0)
);
GO

CREATE NONCLUSTERED INDEX [IX_OrderLines_OrderId]
    ON [dbo].[OrderLines] ([OrderId])
    INCLUDE ([ProductName], [Status], [Quantity], [LineTotal]);
GO

-- ============================================================================
-- 6. SHIPMENTS (Header)
-- ============================================================================
CREATE TABLE [dbo].[Shipments] (
    [ShipmentId]            UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [TenantId]              UNIQUEIDENTIFIER    NOT NULL,
    [OrderId]               UNIQUEIDENTIFIER    NOT NULL,
    [Carrier]               NVARCHAR(100)       NULL,
    [CarrierNormalized]     NVARCHAR(50)        NULL,      -- UPS | FedEx | USPS | DHL | Amazon | Other
    [TrackingNumber]        NVARCHAR(200)       NULL,
    [TrackingUrl]           NVARCHAR(2000)      NULL,
    [ShipDate]              DATETIME2           NULL,
    [EstimatedDelivery]     DATE                NULL,
    [Status]                NVARCHAR(50)        NOT NULL DEFAULT 'Shipped',
    [LastStatusUpdate]      NVARCHAR(500)       NULL,
    [LastStatusDate]        DATETIME2           NULL,
    [SourceEmailId]         UNIQUEIDENTIFIER    NULL,
    [CreatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_Shipments] PRIMARY KEY CLUSTERED ([ShipmentId]),
    CONSTRAINT [FK_Shipments_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [FK_Shipments_Orders] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId]),
    CONSTRAINT [FK_Shipments_SourceEmail] FOREIGN KEY ([SourceEmailId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId]),
    CONSTRAINT [CK_Shipments_Status] CHECK ([Status] IN (
        'Shipped', 'InTransit', 'OutForDelivery', 'Delivered', 'Exception', 'Returned'
    ))
);
GO

CREATE NONCLUSTERED INDEX [IX_Shipments_TenantId_Status]
    ON [dbo].[Shipments] ([TenantId], [Status])
    INCLUDE ([OrderId], [Carrier], [TrackingNumber], [EstimatedDelivery]);
GO

CREATE NONCLUSTERED INDEX [IX_Shipments_OrderId]
    ON [dbo].[Shipments] ([OrderId]);
GO

CREATE NONCLUSTERED INDEX [IX_Shipments_TenantId_TrackingNumber]
    ON [dbo].[Shipments] ([TenantId], [TrackingNumber])
    WHERE [TrackingNumber] IS NOT NULL;
GO

-- ============================================================================
-- 7. SHIPMENT LINES
-- ============================================================================
CREATE TABLE [dbo].[ShipmentLines] (
    [ShipmentLineId]    UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [ShipmentId]        UNIQUEIDENTIFIER    NOT NULL,
    [OrderLineId]       UNIQUEIDENTIFIER    NOT NULL,
    [Quantity]          INT                 NOT NULL DEFAULT 1,

    CONSTRAINT [PK_ShipmentLines] PRIMARY KEY CLUSTERED ([ShipmentLineId]),
    CONSTRAINT [FK_ShipmentLines_Shipments] FOREIGN KEY ([ShipmentId]) REFERENCES [dbo].[Shipments] ([ShipmentId]) ON DELETE CASCADE,
    CONSTRAINT [FK_ShipmentLines_OrderLines] FOREIGN KEY ([OrderLineId]) REFERENCES [dbo].[OrderLines] ([OrderLineId]),
    CONSTRAINT [CK_ShipmentLines_Quantity] CHECK ([Quantity] > 0)
);
GO

CREATE NONCLUSTERED INDEX [IX_ShipmentLines_ShipmentId]
    ON [dbo].[ShipmentLines] ([ShipmentId]);
GO

CREATE NONCLUSTERED INDEX [IX_ShipmentLines_OrderLineId]
    ON [dbo].[ShipmentLines] ([OrderLineId]);
GO

-- ============================================================================
-- 8. DELIVERIES
-- ============================================================================
CREATE TABLE [dbo].[Deliveries] (
    [DeliveryId]            UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [TenantId]              UNIQUEIDENTIFIER    NOT NULL,
    [ShipmentId]            UNIQUEIDENTIFIER    NOT NULL,
    [DeliveryDate]          DATETIME2           NULL,
    [DeliveryLocation]      NVARCHAR(200)       NULL,      -- "Front door", "Mailroom", "Signed: J. Smith"
    [Status]                NVARCHAR(50)        NOT NULL DEFAULT 'Delivered',
    [IssueType]             NVARCHAR(100)       NULL,      -- Missing | Damaged | WrongItem | NotReceived
    [IssueDescription]      NVARCHAR(1000)      NULL,
    [PhotoBlobUrl]          NVARCHAR(2000)      NULL,      -- Delivery photo URL
    [SourceEmailId]         UNIQUEIDENTIFIER    NULL,
    [CreatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_Deliveries] PRIMARY KEY CLUSTERED ([DeliveryId]),
    CONSTRAINT [FK_Deliveries_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [FK_Deliveries_Shipments] FOREIGN KEY ([ShipmentId]) REFERENCES [dbo].[Shipments] ([ShipmentId]),
    CONSTRAINT [FK_Deliveries_SourceEmail] FOREIGN KEY ([SourceEmailId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId]),
    CONSTRAINT [CK_Deliveries_Status] CHECK ([Status] IN (
        'Delivered', 'AttemptedDelivery', 'DeliveryException', 'Lost'
    )),
    CONSTRAINT [CK_Deliveries_IssueType] CHECK ([IssueType] IS NULL OR [IssueType] IN (
        'Missing', 'Damaged', 'WrongItem', 'NotReceived', 'Stolen', 'Other'
    ))
);
GO

CREATE NONCLUSTERED INDEX [IX_Deliveries_ShipmentId]
    ON [dbo].[Deliveries] ([ShipmentId]);
GO

CREATE NONCLUSTERED INDEX [IX_Deliveries_TenantId_Status]
    ON [dbo].[Deliveries] ([TenantId], [Status]);
GO

-- ============================================================================
-- 9. RETURNS (Header)
-- ============================================================================
CREATE TABLE [dbo].[Returns] (
    [ReturnId]              UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [TenantId]              UNIQUEIDENTIFIER    NOT NULL,
    [OrderId]               UNIQUEIDENTIFIER    NOT NULL,
    [RMANumber]             NVARCHAR(200)       NULL,
    [Status]                NVARCHAR(50)        NOT NULL DEFAULT 'Initiated',
    [ReturnReason]          NVARCHAR(500)       NULL,
    [ReturnMethod]          NVARCHAR(100)       NULL,      -- Mail | DropOff | Pickup
    [ReturnCarrier]         NVARCHAR(100)       NULL,
    [ReturnTrackingNumber]  NVARCHAR(200)       NULL,
    [ReturnTrackingUrl]     NVARCHAR(2000)      NULL,
    [ReturnLabelBlobUrl]    NVARCHAR(2000)      NULL,
    [QRCodeBlobUrl]         NVARCHAR(2000)      NULL,
    [QRCodeData]            NVARCHAR(MAX)       NULL,      -- Raw QR data for rendering in UI
    [DropOffLocation]       NVARCHAR(500)       NULL,
    [DropOffAddress]        NVARCHAR(500)       NULL,
    [ReturnByDate]          DATE                NULL,
    [ReceivedByRetailerDate] DATE               NULL,
    [RejectionReason]       NVARCHAR(500)       NULL,
    [SourceEmailId]         UNIQUEIDENTIFIER    NULL,
    [LastUpdatedEmailId]    UNIQUEIDENTIFIER    NULL,
    [CreatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_Returns] PRIMARY KEY CLUSTERED ([ReturnId]),
    CONSTRAINT [FK_Returns_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [FK_Returns_Orders] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId]),
    CONSTRAINT [FK_Returns_SourceEmail] FOREIGN KEY ([SourceEmailId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId]),
    CONSTRAINT [FK_Returns_LastEmail] FOREIGN KEY ([LastUpdatedEmailId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId]),
    CONSTRAINT [CK_Returns_Status] CHECK ([Status] IN (
        'Initiated', 'LabelIssued', 'Shipped', 'Received', 'Rejected', 'RefundPending', 'Refunded', 'Closed'
    )),
    CONSTRAINT [CK_Returns_ReturnMethod] CHECK ([ReturnMethod] IS NULL OR [ReturnMethod] IN ('Mail', 'DropOff', 'Pickup'))
);
GO

CREATE NONCLUSTERED INDEX [IX_Returns_TenantId_Status]
    ON [dbo].[Returns] ([TenantId], [Status])
    INCLUDE ([OrderId], [RMANumber], [ReturnByDate]);
GO

CREATE NONCLUSTERED INDEX [IX_Returns_OrderId]
    ON [dbo].[Returns] ([OrderId]);
GO

CREATE NONCLUSTERED INDEX [IX_Returns_TenantId_ReturnByDate]
    ON [dbo].[Returns] ([TenantId], [ReturnByDate])
    WHERE [Status] IN ('Initiated', 'LabelIssued')
    INCLUDE ([OrderId], [RMANumber]);
GO

-- ============================================================================
-- 10. RETURN LINES
-- ============================================================================
CREATE TABLE [dbo].[ReturnLines] (
    [ReturnLineId]      UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [ReturnId]          UNIQUEIDENTIFIER    NOT NULL,
    [OrderLineId]       UNIQUEIDENTIFIER    NOT NULL,
    [Quantity]          INT                 NOT NULL DEFAULT 1,
    [ReturnReason]      NVARCHAR(500)       NULL,      -- Line-level reason may differ

    CONSTRAINT [PK_ReturnLines] PRIMARY KEY CLUSTERED ([ReturnLineId]),
    CONSTRAINT [FK_ReturnLines_Returns] FOREIGN KEY ([ReturnId]) REFERENCES [dbo].[Returns] ([ReturnId]) ON DELETE CASCADE,
    CONSTRAINT [FK_ReturnLines_OrderLines] FOREIGN KEY ([OrderLineId]) REFERENCES [dbo].[OrderLines] ([OrderLineId]),
    CONSTRAINT [CK_ReturnLines_Quantity] CHECK ([Quantity] > 0)
);
GO

CREATE NONCLUSTERED INDEX [IX_ReturnLines_ReturnId]
    ON [dbo].[ReturnLines] ([ReturnId]);
GO

CREATE NONCLUSTERED INDEX [IX_ReturnLines_OrderLineId]
    ON [dbo].[ReturnLines] ([OrderLineId]);
GO

-- ============================================================================
-- 11. REFUNDS
-- ============================================================================
CREATE TABLE [dbo].[Refunds] (
    [RefundId]              UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [TenantId]              UNIQUEIDENTIFIER    NOT NULL,
    [OrderId]               UNIQUEIDENTIFIER    NOT NULL,
    [ReturnId]              UNIQUEIDENTIFIER    NULL,      -- NULL for cancellation refunds
    [RefundAmount]          DECIMAL(10,2)       NOT NULL,
    [Currency]              NVARCHAR(3)         NOT NULL DEFAULT 'USD',
    [RefundMethod]          NVARCHAR(100)       NULL,      -- "Original payment", "Store credit", etc.
    [RefundDate]            DATETIME2           NULL,
    [EstimatedArrival]      NVARCHAR(200)       NULL,      -- e.g. "5-10 business days"
    [TransactionId]         NVARCHAR(200)       NULL,      -- Payment processor reference
    [SourceEmailId]         UNIQUEIDENTIFIER    NULL,
    [CreatedAt]             DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_Refunds] PRIMARY KEY CLUSTERED ([RefundId]),
    CONSTRAINT [FK_Refunds_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [FK_Refunds_Orders] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId]),
    CONSTRAINT [FK_Refunds_Returns] FOREIGN KEY ([ReturnId]) REFERENCES [dbo].[Returns] ([ReturnId]),
    CONSTRAINT [FK_Refunds_SourceEmail] FOREIGN KEY ([SourceEmailId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId]),
    CONSTRAINT [CK_Refunds_Amount] CHECK ([RefundAmount] > 0)
);
GO

CREATE NONCLUSTERED INDEX [IX_Refunds_TenantId]
    ON [dbo].[Refunds] ([TenantId])
    INCLUDE ([OrderId], [ReturnId], [RefundAmount], [RefundDate]);
GO

CREATE NONCLUSTERED INDEX [IX_Refunds_OrderId]
    ON [dbo].[Refunds] ([OrderId]);
GO

CREATE NONCLUSTERED INDEX [IX_Refunds_ReturnId]
    ON [dbo].[Refunds] ([ReturnId])
    WHERE [ReturnId] IS NOT NULL;
GO

-- ============================================================================
-- 12. ORDER EVENTS (Audit / Timeline)
-- ============================================================================
CREATE TABLE [dbo].[OrderEvents] (
    [EventId]           UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [TenantId]          UNIQUEIDENTIFIER    NOT NULL,
    [OrderId]           UNIQUEIDENTIFIER    NOT NULL,
    [EventType]         NVARCHAR(50)        NOT NULL,
    [EventDate]         DATETIME2           NOT NULL,
    [Summary]           NVARCHAR(1000)      NOT NULL,
    [Details]           NVARCHAR(MAX)       NULL,      -- JSON blob for additional structured data
    [EmailMessageId]    UNIQUEIDENTIFIER    NULL,      -- NULL for system-generated events
    [EntityType]        NVARCHAR(50)        NULL,      -- Shipment | Delivery | Return | Refund | Order
    [EntityId]          UNIQUEIDENTIFIER    NULL,
    [CreatedAt]         DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_OrderEvents] PRIMARY KEY CLUSTERED ([EventId]),
    CONSTRAINT [FK_OrderEvents_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [FK_OrderEvents_Orders] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId]),
    CONSTRAINT [FK_OrderEvents_EmailMessages] FOREIGN KEY ([EmailMessageId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId])
);
GO

CREATE NONCLUSTERED INDEX [IX_OrderEvents_OrderId_EventDate]
    ON [dbo].[OrderEvents] ([OrderId], [EventDate] DESC)
    INCLUDE ([EventType], [Summary]);
GO

CREATE NONCLUSTERED INDEX [IX_OrderEvents_TenantId_EventDate]
    ON [dbo].[OrderEvents] ([TenantId], [EventDate] DESC)
    INCLUDE ([OrderId], [EventType], [Summary]);
GO

-- ============================================================================
-- 13. PROCESSING CORRECTIONS (for AI model improvement)
-- ============================================================================
CREATE TABLE [dbo].[ProcessingCorrections] (
    [CorrectionId]          UNIQUEIDENTIFIER    NOT NULL DEFAULT NEWID(),
    [TenantId]              UNIQUEIDENTIFIER    NOT NULL,
    [EmailMessageId]        UNIQUEIDENTIFIER    NOT NULL,
    [OriginalClassification] NVARCHAR(50)       NULL,
    [CorrectedClassification] NVARCHAR(50)      NULL,
    [OriginalParsedData]    NVARCHAR(MAX)       NULL,      -- JSON
    [CorrectedParsedData]   NVARCHAR(MAX)       NULL,      -- JSON
    [CorrectionType]        NVARCHAR(50)        NOT NULL,  -- Classification | Parsing | Dismissed
    [CorrectedAt]           DATETIME2           NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_ProcessingCorrections] PRIMARY KEY CLUSTERED ([CorrectionId]),
    CONSTRAINT [FK_ProcessingCorrections_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [FK_ProcessingCorrections_EmailMessages] FOREIGN KEY ([EmailMessageId]) REFERENCES [dbo].[EmailMessages] ([EmailMessageId])
);
GO

PRINT 'All tables created successfully.';
GO
