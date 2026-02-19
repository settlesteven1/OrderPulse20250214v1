-- ============================================================================
-- Migration 007: Add OriginalFromAddress to EmailMessages
-- Supports forwarded email detection — stores the original sender address
-- extracted from internet message headers or forwarded email body patterns.
-- ============================================================================

ALTER TABLE [dbo].[EmailMessages]
    ADD [OriginalFromAddress] NVARCHAR(320) NULL;
GO
