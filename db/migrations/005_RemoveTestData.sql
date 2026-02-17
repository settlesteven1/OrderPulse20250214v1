-- ============================================================================
-- OrderPulse: Remove Test Data
-- Migration 005: Clean out seeded test data, keep schema and retailers
-- Run with RLS disabled or with SESSION_CONTEXT set
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- Set session context to allow deletes through RLS (if still enabled)
DECLARE @TenantId UNIQUEIDENTIFIER = '215F9D63-05C2-4C4C-8548-1CD950DC430A';
EXEC sp_set_session_context @key=N'TenantId', @value=@TenantId;

-- Delete in dependency order (children first)
DELETE FROM OrderEvents;
DELETE FROM Refunds;
DELETE FROM Returns;
DELETE FROM Deliveries;
DELETE FROM Shipments;
DELETE FROM OrderLines;
DELETE FROM Orders;
DELETE FROM EmailMessages;

-- Reset LastSyncAt so email poller re-ingests
UPDATE Tenants SET LastSyncAt = NULL WHERE TenantId = @TenantId;

PRINT 'Test data removed. Email pipeline reset.';
