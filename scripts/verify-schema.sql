-- ============================================================================
-- OrderPulse Schema Verification Script
-- Run against sqldb-orderpulse to verify all migrations applied correctly
-- ============================================================================

PRINT '=== OrderPulse Schema Verification ==='
PRINT ''

-- 1. Check all 13 expected tables exist
PRINT '--- Tables ---'
SELECT
    t.TABLE_SCHEMA + '.' + t.TABLE_NAME AS [Table],
    (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_NAME = t.TABLE_NAME AND c.TABLE_SCHEMA = t.TABLE_SCHEMA) AS [Columns],
    CASE WHEN p.rows IS NOT NULL THEN p.rows ELSE 0 END AS [Rows]
FROM INFORMATION_SCHEMA.TABLES t
LEFT JOIN sys.partitions p ON p.object_id = OBJECT_ID(t.TABLE_SCHEMA + '.' + t.TABLE_NAME) AND p.index_id IN (0, 1)
WHERE t.TABLE_TYPE = 'BASE TABLE'
ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME;

-- 2. Verify expected table count
DECLARE @tableCount INT;
SELECT @tableCount = COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE';
PRINT '';
PRINT 'Total tables: ' + CAST(@tableCount AS VARCHAR);
IF @tableCount >= 13
    PRINT 'PASS: Expected at least 13 tables'
ELSE
    PRINT 'FAIL: Expected at least 13 tables, found ' + CAST(@tableCount AS VARCHAR);

-- 3. Check each expected table has key columns
PRINT '';
PRINT '--- Key Column Checks ---';

-- Tenants
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Tenants' AND COLUMN_NAME = 'Id')
    PRINT 'PASS: Tenants.Id exists'
ELSE
    PRINT 'FAIL: Tenants.Id missing';

-- Orders
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Orders' AND COLUMN_NAME = 'TenantId')
    PRINT 'PASS: Orders.TenantId exists'
ELSE
    PRINT 'FAIL: Orders.TenantId missing';

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Orders' AND COLUMN_NAME = 'Status')
    PRINT 'PASS: Orders.Status exists'
ELSE
    PRINT 'FAIL: Orders.Status missing';

-- OrderLines
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'OrderLines' AND COLUMN_NAME = 'OrderId')
    PRINT 'PASS: OrderLines.OrderId exists'
ELSE
    PRINT 'FAIL: OrderLines.OrderId missing';

-- Shipments
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Shipments' AND COLUMN_NAME = 'TrackingNumber')
    PRINT 'PASS: Shipments.TrackingNumber exists'
ELSE
    PRINT 'FAIL: Shipments.TrackingNumber missing';

-- Returns
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Returns' AND COLUMN_NAME = 'ReturnLabelUrl')
    PRINT 'PASS: Returns.ReturnLabelUrl exists'
ELSE
    PRINT 'FAIL: Returns.ReturnLabelUrl missing';

-- Refunds
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Refunds' AND COLUMN_NAME = 'Amount')
    PRINT 'PASS: Refunds.Amount exists'
ELSE
    PRINT 'FAIL: Refunds.Amount missing';

-- EmailMessages
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'EmailMessages' AND COLUMN_NAME = 'ClassificationType')
    PRINT 'PASS: EmailMessages.ClassificationType exists'
ELSE
    PRINT 'FAIL: EmailMessages.ClassificationType missing';

-- 4. Check foreign keys
PRINT '';
PRINT '--- Foreign Keys ---';
SELECT
    fk.name AS [FK Name],
    OBJECT_NAME(fk.parent_object_id) AS [Table],
    COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS [Column],
    OBJECT_NAME(fk.referenced_object_id) AS [References]
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
ORDER BY OBJECT_NAME(fk.parent_object_id), fk.name;

DECLARE @fkCount INT;
SELECT @fkCount = COUNT(*) FROM sys.foreign_keys;
PRINT '';
PRINT 'Total foreign keys: ' + CAST(@fkCount AS VARCHAR);

-- 5. Check indexes
PRINT '';
PRINT '--- Indexes (non-PK) ---';
SELECT
    OBJECT_NAME(i.object_id) AS [Table],
    i.name AS [Index],
    i.type_desc AS [Type],
    i.is_unique AS [Unique]
FROM sys.indexes i
WHERE i.name IS NOT NULL
  AND i.is_primary_key = 0
  AND OBJECT_SCHEMA_NAME(i.object_id) != 'sys'
ORDER BY OBJECT_NAME(i.object_id), i.name;

-- 6. Check RLS (migration 002)
PRINT '';
PRINT '--- Row-Level Security ---';
IF EXISTS (SELECT 1 FROM sys.security_policies)
BEGIN
    SELECT name AS [Policy], is_enabled AS [Enabled] FROM sys.security_policies;
    PRINT 'PASS: RLS policies found'
END
ELSE
    PRINT 'WARNING: No RLS policies found (migration 002 may not have run)';

IF EXISTS (SELECT 1 FROM sys.objects WHERE name = 'fn_TenantAccessPredicate' AND type = 'IF')
    PRINT 'PASS: fn_TenantAccessPredicate function exists'
ELSE
    PRINT 'WARNING: fn_TenantAccessPredicate missing';

-- 7. Check seed data (migration 003)
PRINT '';
PRINT '--- Seed Data ---';
DECLARE @retailerCount INT;
SELECT @retailerCount = COUNT(*) FROM dbo.Retailers;
PRINT 'Retailers seeded: ' + CAST(@retailerCount AS VARCHAR);
IF @retailerCount >= 20
    PRINT 'PASS: Expected at least 20 retailers'
ELSE
    PRINT 'WARNING: Expected 20 retailers, found ' + CAST(@retailerCount AS VARCHAR) + ' (migration 003 may not have run)';

PRINT '';
PRINT '=== Verification Complete ==='
