-- ============================================================================
-- OrderPulse Database Schema
-- Migration 002: Row-Level Security policies
--
-- RLS ensures that even if the API layer has a bug, a tenant can never
-- see another tenant's data. The TenantId is set via SESSION_CONTEXT
-- at the beginning of each database connection in the EF Core DbContext.
-- ============================================================================

-- Create a schema for security predicates
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Security')
    EXEC('CREATE SCHEMA [Security]');
GO

-- ============================================================================
-- Security predicate function
-- Returns 1 (allow) when the row's TenantId matches SESSION_CONTEXT
-- ============================================================================
CREATE OR ALTER FUNCTION [Security].[fn_TenantAccessPredicate]
(
    @TenantId UNIQUEIDENTIFIER
)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN SELECT 1 AS [fn_result]
    WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS UNIQUEIDENTIFIER);
GO

-- ============================================================================
-- Apply filter policies to all tenant-scoped tables
-- FILTER PREDICATE: hides rows from other tenants on SELECT
-- BLOCK PREDICATE: prevents INSERT/UPDATE of rows with wrong TenantId
-- ============================================================================

-- EmailMessages
CREATE SECURITY POLICY [Security].[EmailMessagesPolicy]
    ADD FILTER PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[EmailMessages],
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[EmailMessages] AFTER INSERT,
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[EmailMessages] AFTER UPDATE
    WITH (STATE = ON);
GO

-- Orders
CREATE SECURITY POLICY [Security].[OrdersPolicy]
    ADD FILTER PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Orders],
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Orders] AFTER INSERT,
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Orders] AFTER UPDATE
    WITH (STATE = ON);
GO

-- Shipments
CREATE SECURITY POLICY [Security].[ShipmentsPolicy]
    ADD FILTER PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Shipments],
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Shipments] AFTER INSERT,
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Shipments] AFTER UPDATE
    WITH (STATE = ON);
GO

-- Deliveries
CREATE SECURITY POLICY [Security].[DeliveriesPolicy]
    ADD FILTER PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Deliveries],
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Deliveries] AFTER INSERT,
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Deliveries] AFTER UPDATE
    WITH (STATE = ON);
GO

-- Returns
CREATE SECURITY POLICY [Security].[ReturnsPolicy]
    ADD FILTER PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Returns],
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Returns] AFTER INSERT,
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Returns] AFTER UPDATE
    WITH (STATE = ON);
GO

-- Refunds
CREATE SECURITY POLICY [Security].[RefundsPolicy]
    ADD FILTER PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Refunds],
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Refunds] AFTER INSERT,
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[Refunds] AFTER UPDATE
    WITH (STATE = ON);
GO

-- OrderEvents
CREATE SECURITY POLICY [Security].[OrderEventsPolicy]
    ADD FILTER PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[OrderEvents],
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[OrderEvents] AFTER INSERT,
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[OrderEvents] AFTER UPDATE
    WITH (STATE = ON);
GO

-- ProcessingCorrections
CREATE SECURITY POLICY [Security].[ProcessingCorrectionsPolicy]
    ADD FILTER PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[ProcessingCorrections],
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[ProcessingCorrections] AFTER INSERT,
    ADD BLOCK PREDICATE [Security].[fn_TenantAccessPredicate]([TenantId])
        ON [dbo].[ProcessingCorrections] AFTER UPDATE
    WITH (STATE = ON);
GO

PRINT 'Row-Level Security policies created successfully.';
PRINT 'Remember: Set SESSION_CONTEXT TenantId in your DbContext connection interceptor.';
GO
