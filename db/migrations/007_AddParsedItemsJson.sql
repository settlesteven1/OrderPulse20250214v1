-- ============================================================================
-- OrderPulse Schema Update
-- Migration 007: Add ParsedItemsJson columns for reconciliation support
-- Supports Issue #26: Optimistic processing with reconciliation
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- Add ParsedItemsJson to Shipments
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Shipments') AND name = 'ParsedItemsJson')
BEGIN
    ALTER TABLE [dbo].[Shipments]
        ADD [ParsedItemsJson] NVARCHAR(MAX) NULL;
END
GO

-- Add ParsedItemsJson to Returns
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Returns') AND name = 'ParsedItemsJson')
BEGIN
    ALTER TABLE [dbo].[Returns]
        ADD [ParsedItemsJson] NVARCHAR(MAX) NULL;
END
GO

PRINT 'Migration 007: ParsedItemsJson columns added successfully.';
GO
