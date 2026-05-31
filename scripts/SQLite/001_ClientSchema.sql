-- SyncSystem SQLite Client Schema
-- Session 14: Added ModifiedByUserId for multi-user audit tracking

-- Local sync state
CREATE TABLE IF NOT EXISTS LocalSyncState (
    TableName TEXT PRIMARY KEY,
    LastSyncVersion INTEGER NOT NULL DEFAULT 0,
    LastSyncCompletedAtUtc TEXT NULL
);

-- Example: Customers table (client-side)
CREATE TABLE IF NOT EXISTS Customers (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Email TEXT NOT NULL,
    Phone TEXT NULL,
    Address TEXT NULL,
    SyncVersion INTEGER NOT NULL DEFAULT 0,
    ModifiedAtUtc TEXT NOT NULL DEFAULT (datetime('now')),
    ServerModifiedAtUtc TEXT NULL,
    ModifiedByUserId TEXT NOT NULL DEFAULT 'Local',
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    IsDirty INTEGER NOT NULL DEFAULT 0,
    CreatedAtUtc TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_customers_dirty ON Customers(IsDirty) WHERE IsDirty = 1;
CREATE INDEX IF NOT EXISTS idx_customers_sync ON Customers(SyncVersion);

-- Sync log
CREATE TABLE IF NOT EXISTS SyncLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId TEXT NULL,
    Component TEXT NOT NULL,
    Level TEXT NOT NULL,
    Message TEXT NOT NULL,
    Exception TEXT NULL,
    CreatedAtUtc TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Initialize metadata
INSERT OR IGNORE INTO LocalSyncState (TableName, LastSyncVersion) VALUES ('Customers', 0);
