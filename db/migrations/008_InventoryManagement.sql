-- ============================================================================
-- OrderPulse Database Schema
-- Migration 008: Inventory Management
--   - Add ItemCategory to OrderLines
--   - Create InventoryItems table
--   - Create InventoryAdjustments table
--   - Add RLS policies for new tables
-- Target: Azure SQL Database
-- ============================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================================
-- 1. ALTER OrderLines — add AI-classified item category
-- ============================================================================
ALTER TABLE [dbo].[OrderLines]
    ADD [ItemCategory] NVARCHAR(20) NULL;
GO

-- ============================================================================
-- 2. InventoryItems
-- ============================================================================
CREATE TABLE [dbo].[InventoryItems] (
    [InventoryItemId]   UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [TenantId]          UNIQUEIDENTIFIER NOT NULL,
    [OrderLineId]       UNIQUEIDENTIFIER NOT NULL,
    [OrderId]           UNIQUEIDENTIFIER NOT NULL,
    [ProductName]       NVARCHAR(500)    NOT NULL,
    [ItemCategory]      NVARCHAR(20)     NOT NULL,
    [QuantityOnHand]    INT              NOT NULL DEFAULT 0,
    [UnitStatus]        NVARCHAR(50)     NULL,
    [Condition]         NVARCHAR(50)     NULL DEFAULT 'New',
    [PurchaseDate]      DATETIME2        NULL,
    [DeliveryDate]      DATETIME2        NULL,
    [CreatedAt]         DATETIME2        NOT NULL DEFAULT GETUTCDATE(),
    [UpdatedAt]         DATETIME2        NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_InventoryItems] PRIMARY KEY CLUSTERED ([InventoryItemId]),
    CONSTRAINT [FK_InventoryItems_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId]),
    CONSTRAINT [FK_InventoryItems_OrderLines] FOREIGN KEY ([OrderLineId]) REFERENCES [dbo].[OrderLines] ([OrderLineId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_InventoryItems_Orders] FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId]) ON DELETE NO ACTION,
    CONSTRAINT [CK_InventoryItems_ItemCategory] CHECK ([ItemCategory] IN ('Durable', 'Consumable'))
);
GO

CREATE NONCLUSTERED INDEX [IX_InventoryItems_TenantId] ON [dbo].[InventoryItems] ([TenantId]);
GO
CREATE NONCLUSTERED INDEX [IX_InventoryItems_OrderLineId] ON [dbo].[InventoryItems] ([OrderLineId]);
GO
CREATE NONCLUSTERED INDEX [IX_InventoryItems_TenantId_ItemCategory] ON [dbo].[InventoryItems] ([TenantId], [ItemCategory]);
GO

-- ============================================================================
-- 3. InventoryAdjustments
-- ============================================================================
CREATE TABLE [dbo].[InventoryAdjustments] (
    [AdjustmentId]      UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [InventoryItemId]   UNIQUEIDENTIFIER NOT NULL,
    [TenantId]          UNIQUEIDENTIFIER NOT NULL,
    [QuantityDelta]     INT              NOT NULL,
    [PreviousQuantity]  INT              NOT NULL,
    [NewQuantity]       INT              NOT NULL,
    [Reason]            NVARCHAR(100)    NOT NULL,
    [Notes]             NVARCHAR(500)    NULL,
    [AdjustedBy]        NVARCHAR(200)    NULL,
    [AdjustedAt]        DATETIME2        NOT NULL DEFAULT GETUTCDATE(),

    CONSTRAINT [PK_InventoryAdjustments] PRIMARY KEY CLUSTERED ([AdjustmentId]),
    CONSTRAINT [FK_InventoryAdjustments_InventoryItems] FOREIGN KEY ([InventoryItemId]) REFERENCES [dbo].[InventoryItems] ([InventoryItemId]) ON DELETE CASCADE,
    CONSTRAINT [FK_InventoryAdjustments_Tenants] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([TenantId])
);
GO

CREATE NONCLUSTERED INDEX [IX_InventoryAdjustments_InventoryItemId] ON [dbo].[InventoryAdjustments] ([InventoryItemId]);
GO
CREATE NONCLUSTERED INDEX [IX_InventoryAdjustments_TenantId] ON [dbo].[InventoryAdjustments] ([TenantId]);
GO

-- ============================================================================
-- 4. RLS Policies for new tables
-- ============================================================================

-- InventoryItems
CREATE SECURITY POLICY [dbo].[InventoryItemsFilter]
    ADD FILTER PREDICATE [dbo].[fn_tenantAccessPredicate]([TenantId]) ON [dbo].[InventoryItems],
    ADD BLOCK  PREDICATE [dbo].[fn_tenantAccessPredicate]([TenantId]) ON [dbo].[InventoryItems]
    WITH (STATE = ON);
GO

-- InventoryAdjustments
CREATE SECURITY POLICY [dbo].[InventoryAdjustmentsFilter]
    ADD FILTER PREDICATE [dbo].[fn_tenantAccessPredicate]([TenantId]) ON [dbo].[InventoryAdjustments],
    ADD BLOCK  PREDICATE [dbo].[fn_tenantAccessPredicate]([TenantId]) ON [dbo].[InventoryAdjustments]
    WITH (STATE = ON);
GO

PRINT 'Migration 008_InventoryManagement completed successfully.';
GO
