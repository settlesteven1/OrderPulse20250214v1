-- ============================================================================
-- OrderPulse Processing Log
-- Migration 006: Diagnostic log table for email processing pipeline
-- No RLS - queryable by anyone for debugging
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

CREATE TABLE ProcessingLog (
    LogId           INT IDENTITY(1,1) PRIMARY KEY,
    Timestamp       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    EmailMessageId  UNIQUEIDENTIFIER NULL,
    Step            NVARCHAR(100) NOT NULL,
    Status          NVARCHAR(20) NOT NULL,  -- 'Info', 'Success', 'Warning', 'Error'
    Message         NVARCHAR(MAX) NOT NULL,
    Details         NVARCHAR(MAX) NULL
);

-- Index for quick lookup by email
CREATE INDEX IX_ProcessingLog_EmailMessageId ON ProcessingLog (EmailMessageId);
CREATE INDEX IX_ProcessingLog_Timestamp ON ProcessingLog (Timestamp DESC);
