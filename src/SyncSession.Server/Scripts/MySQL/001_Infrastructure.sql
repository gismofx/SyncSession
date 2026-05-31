-- ============================================================================
-- SyncSession - MySQL Infrastructure Schema (REQUIRED)
-- Version: 1.0.0
-- Purpose: Core sync infrastructure tables - required for all deployments
-- ============================================================================
-- NOTE: This script assumes the database already exists.
--       Create your database first with your preferred name and collation.
-- ============================================================================

-- ============================================================================
-- INFRASTRUCTURE TABLES (Sync System Core)
-- ============================================================================

-- ----------------------------------------------------------------------------
-- SessionRecords: Tracks all sync sessions (push and pull)
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS SessionRecords (
    SessionId CHAR(36) PRIMARY KEY,
    TenantId CHAR(36) NULL,             -- NULL = non-tenant / single-tenant deployments
    DeviceId CHAR(36) NULL,             -- Device that initiated this session (38l)
    UserId VARCHAR(255) NULL,           -- Authenticated user from token claims (38l)
    UserDisplayName VARCHAR(255) NULL,  -- Display name from configured claim type (38l)
    SessionType VARCHAR(20) NOT NULL,   -- 'Push', 'Pull', or 'Seed'
    Status VARCHAR(20) NOT NULL,        -- 'Staging', 'Ready', 'Processing', 'Committed', 'Completed', 'Failed'
    SyncVersion BIGINT NOT NULL AUTO_INCREMENT UNIQUE,  -- Atomic version assignment
    CreatedAtUtc DATETIME(6) NOT NULL,
    LastActivityUtc DATETIME(6) NOT NULL,
    CommittedAtUtc DATETIME(6) NULL,
    ErrorMessage TEXT NULL,
    TotalRows INT NOT NULL DEFAULT 0,   -- Total records synced/seeded (38l)
    RowCountsJson TEXT NULL,            -- Per-table breakdown: {"TableA":100,"TableB":200} (38l)
    INDEX IX_SessionRecords_Status (Status),
    INDEX IX_SessionRecords_Tenant (TenantId),
    INDEX IX_SessionRecords_Version (SyncVersion),
    INDEX IX_SessionRecords_Created (CreatedAtUtc)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- SyncSessionTables: Tracks tables within each session
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS SyncSessionTables (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    SessionId CHAR(36) NOT NULL,
    TableName VARCHAR(255) NOT NULL,
    TempTableName VARCHAR(255) NOT NULL,
    ProcessingPriority INT NOT NULL,       -- Processing order (FK constraints)
    UsesSharedTable BOOLEAN NOT NULL,   -- TRUE = shared, FALSE = dedicated
    Status VARCHAR(20) NOT NULL DEFAULT 'Staging',
    EstimatedRecordCount INT NULL,
    ActualRecordCount INT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX IX_SyncSessionTables_Session (SessionId),
    INDEX IX_SyncSessionTables_Table (TableName),
    FOREIGN KEY (SessionId) REFERENCES SessionRecords(SessionId) ON DELETE CASCADE
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- ClientProcessedSessions: Tracks which sessions each device has pulled
-- Critical for "no lost records" guarantee
-- Session 16g: Changed to DeviceId for multi-device per user support
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ClientProcessedSessions (
    DeviceId CHAR(36) NOT NULL,
    SessionId CHAR(36) NOT NULL,
    ProcessedAtUtc DATETIME(6) NOT NULL,
    PRIMARY KEY (DeviceId, SessionId)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- SeedSnapshots: Tracks active seed streaming operations per device+tenant.
-- Enables retry (reuse existing snapshot tables on reconnect) and orphan cleanup.
-- One active snapshot per (DeviceId, TenantId) enforced by unique index.
-- Status: 'Active' | 'Complete' | 'Failed'
-- Snapshot tables are named: SeedSnap_<TableName>_<SeedId>
-- Cleanup: orphans with Status='Active' and LastActivityUtc older than
--          ServerSyncConfiguration.SeedSnapshotOrphanHours (default 4h)
--          are dropped by TempTableCleanupService.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS SeedSnapshots (
    SeedId          CHAR(36)    NOT NULL,
    DeviceId        CHAR(36)    NOT NULL,
    TenantId        CHAR(36)    NOT NULL,
    Status          VARCHAR(20) NOT NULL DEFAULT 'Active',
    CreatedAtUtc    DATETIME(6) NOT NULL,
    LastActivityUtc DATETIME(6) NOT NULL,
    PRIMARY KEY (SeedId),
    UNIQUE INDEX IX_SeedSnapshots_Device_Tenant (DeviceId, TenantId),
    INDEX IX_SeedSnapshots_Status_Activity (Status, LastActivityUtc)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- SyncActivityLog: REMOVED in 38l — audit data now lives on SessionRecords directly.
-- Seed operations create SessionRecords rows with SessionType='Seed'.
-- ----------------------------------------------------------------------------

-- ============================================================================
-- INITIAL DATA
-- ============================================================================

-- (SyncMetadata removed - using AUTO_INCREMENT on SessionRecords.SyncVersion)

-- ============================================================================
-- HELPER VIEWS (for monitoring)
-- ============================================================================

CREATE OR REPLACE VIEW vw_ActiveSessions AS
SELECT 
    s.SessionId,
    s.SessionType,
    s.Status,
    s.CreatedAtUtc,
    s.LastActivityUtc,
    TIMESTAMPDIFF(MINUTE, s.LastActivityUtc, UTC_TIMESTAMP(6)) AS MinutesSinceActivity,
    COUNT(st.Id) AS TableCount
FROM SessionRecords s
LEFT JOIN SyncSessionTables st ON s.SessionId = st.SessionId
WHERE s.Status IN ('Staging', 'Ready', 'Processing')
GROUP BY s.SessionId, s.SessionType, s.Status, s.CreatedAtUtc, s.LastActivityUtc;

CREATE OR REPLACE VIEW vw_QueueDepth AS
SELECT 
    Status,
    COUNT(*) AS SessionCount,
    MIN(CreatedAtUtc) AS OldestSession,
    MAX(CreatedAtUtc) AS NewestSession
FROM SessionRecords
WHERE Status = 'Ready'
GROUP BY Status;

-- ============================================================================
-- DEPLOYMENT NOTES
-- ============================================================================
/*
DEPLOYMENT STEPS:
1. Create database manually with your preferred name and collation:
   
   CREATE DATABASE YourDbName CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
   
   OR use utf8mb4_general_ci for better performance (less accurate sorting)
   OR use your organization's standard collation

2. Update connection string in appsettings.json or user secrets:
   
   "Server=localhost;Database=YourDbName;User=your_user;Password=..."

3. Run this migration (DbUp will execute automatically on server startup)

4. Add your business tables (see 002_ExampleBusinessTables.sql for pattern)

SYNC COLUMNS (required on all synced business tables):
- SyncVersion BIGINT NOT NULL DEFAULT 0
- ModifiedAtUtc DATETIME(6) NOT NULL
- SyncSessionId CHAR(36) NULL
- ModifiedByUserId VARCHAR(100) NOT NULL DEFAULT 'System'
- IsDeleted BOOLEAN NOT NULL DEFAULT FALSE

Indexes needed on synced tables:
- INDEX IX_YourTable_SyncVersion (SyncVersion)
- INDEX IX_YourTable_Session (SyncSessionId)

Optional but recommended:
- TenantId CHAR(36) for multi-tenancy
- CreatedAtUtc DATETIME(6) for audit trail
- INDEX IX_YourTable_Tenant (TenantId)
*/
