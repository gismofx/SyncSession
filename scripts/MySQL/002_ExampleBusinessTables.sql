-- ============================================================================
-- SyncSystem - Example Business Tables (OPTIONAL)
-- Version: 1.0.0
-- Purpose: Demonstrates sync pattern with Customers, Orders, OrderItems
-- NOTE: This is an EXAMPLE only - you will replace with your own schema
-- ============================================================================

-- ============================================================================
-- BUSINESS TABLES (Example Schema)
-- These demonstrate the sync pattern - adapt for your domain
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Products: Shared reference data (no TenantId)
-- Priority = 1
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Products (
    Id CHAR(36) PRIMARY KEY,
    Name VARCHAR(255) NOT NULL,
    SKU VARCHAR(100) NOT NULL,
    Price DECIMAL(18,2) NOT NULL DEFAULT 0,
    
    -- Sync columns (REQUIRED for all synced tables)
    ModifiedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL DEFAULT 'System',
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    
    -- Audit columns (optional but recommended)
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    
    INDEX IX_Products_Session (SyncSessionId),
    INDEX IX_Products_SKU (SKU),
    UNIQUE KEY UQ_Products_SKU (SKU)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- Customers: Example entity with multi-tenant support
-- Priority = 1
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Customers (
    Id CHAR(36) PRIMARY KEY,
    TenantId CHAR(36) NOT NULL,         -- Multi-tenant isolation
    Name VARCHAR(255) NOT NULL,
    Email VARCHAR(255) NULL,
    Phone VARCHAR(50) NULL,
    Address TEXT NULL,
    
    -- Sync columns (REQUIRED for all synced tables)
    ModifiedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL DEFAULT 'System',
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    
    -- Audit columns (optional but recommended)
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    
    INDEX IX_Customers_Tenant (TenantId),
    INDEX IX_Customers_Session (SyncSessionId),
    INDEX IX_Customers_Email (Email)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- Orders: Example entity with FK to Customers
-- Priority = 2 (after Customers)
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Orders (
    Id CHAR(36) PRIMARY KEY,
    TenantId CHAR(36) NOT NULL,
    CustomerId CHAR(36) NOT NULL,
    OrderNumber VARCHAR(50) NOT NULL,
    TotalAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    OrderDate DATETIME(6) NOT NULL,
    Status VARCHAR(50) NOT NULL DEFAULT 'Pending',
    
    -- Sync columns (REQUIRED)
    ModifiedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL DEFAULT 'System',
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    
    -- Audit columns
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    
    INDEX IX_Orders_Tenant (TenantId),
    INDEX IX_Orders_Customer (CustomerId),
    INDEX IX_Orders_Session (SyncSessionId),
    INDEX IX_Orders_OrderNumber (OrderNumber),
    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- OrderItems: Example entity with FK to Orders and Products
-- Priority = 3 (after Orders)
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS OrderItems (
    Id CHAR(36) PRIMARY KEY,
    OrderId CHAR(36) NOT NULL,
    ProductId CHAR(36) NOT NULL,
    ProductName VARCHAR(255) NOT NULL,  -- Denormalized for display
    Quantity INT NOT NULL DEFAULT 1,
    UnitPrice DECIMAL(18,2) NOT NULL,
    
    -- Sync columns (REQUIRED)
    ModifiedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL DEFAULT 'System',
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    
    -- Audit columns
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    
    INDEX IX_OrderItems_Order (OrderId),
    INDEX IX_OrderItems_Product (ProductId),
    INDEX IX_OrderItems_Session (SyncSessionId),
    FOREIGN KEY (OrderId) REFERENCES Orders(Id),
    FOREIGN KEY (ProductId) REFERENCES Products(Id)
) ENGINE=InnoDB;

-- ============================================================================
-- TEMP TABLES (Shared Strategy)
-- Used for small syncs (<10K records by default)
-- NOTE: Create matching temp tables for EACH business table you sync
-- ============================================================================

-- ----------------------------------------------------------------------------
-- TempPushProducts: Staging area for pushed Product records
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS TempPushProducts (
    SequenceNumber INT AUTO_INCREMENT,
    SessionId CHAR(36) NOT NULL,
    Id CHAR(36) NOT NULL,
    Name VARCHAR(255) NOT NULL,
    SKU VARCHAR(100) NOT NULL,
    Price DECIMAL(18,2) NOT NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL,
    ModifiedAtUtc DATETIME(6) NULL,
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (SequenceNumber, SessionId),
    INDEX IX_TempPushProducts_Session (SessionId)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- TempPushCustomers: Staging area for pushed Customer records
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS TempPushCustomers (
    SequenceNumber INT AUTO_INCREMENT,
    SessionId CHAR(36) NOT NULL,
    Id CHAR(36) NOT NULL,
    TenantId CHAR(36) NOT NULL,
    Name VARCHAR(255) NOT NULL,
    Email VARCHAR(255) NULL,
    Phone VARCHAR(50) NULL,
    Address TEXT NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL,
    ModifiedAtUtc DATETIME(6) NULL,
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (SequenceNumber, SessionId),
    INDEX IX_TempPushCustomers_Session (SessionId)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- TempPushOrders: Staging area for pushed Order records
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS TempPushOrders (
    SequenceNumber INT AUTO_INCREMENT,
    SessionId CHAR(36) NOT NULL,
    Id CHAR(36) NOT NULL,
    TenantId CHAR(36) NOT NULL,
    CustomerId CHAR(36) NOT NULL,
    OrderNumber VARCHAR(50) NOT NULL,
    TotalAmount DECIMAL(18,2) NOT NULL,
    OrderDate DATETIME(6) NOT NULL,
    Status VARCHAR(50) NOT NULL,
    ModifiedAtUtc DATETIME(6) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL,
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (SequenceNumber, SessionId),
    INDEX IX_TempPushOrders_Session (SessionId)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- TempPushOrderItems: Staging area for pushed OrderItem records
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS TempPushOrderItems (
    SequenceNumber INT AUTO_INCREMENT,
    SessionId CHAR(36) NOT NULL,
    Id CHAR(36) NOT NULL,
    OrderId CHAR(36) NOT NULL,
    ProductId CHAR(36) NOT NULL,
    ProductName VARCHAR(255) NOT NULL,
    Quantity INT NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    ModifiedAtUtc DATETIME(6) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL,
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (SequenceNumber, SessionId),
    INDEX IX_TempPushOrderItems_Session (SessionId)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- TempPullProducts: Pull snapshot for Products
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS TempPullProducts (
    SessionId CHAR(36) NOT NULL,
    Id CHAR(36) NOT NULL,
    Name VARCHAR(255) NOT NULL,
    SKU VARCHAR(100) NOT NULL,
    Price DECIMAL(18,2) NOT NULL,
    ModifiedAtUtc DATETIME(6) NOT NULL,
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL,
    IsDeleted BOOLEAN NOT NULL,
    PRIMARY KEY (SessionId, Id),
    INDEX IX_TempPullProducts_PullSession (SessionId)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- TempPullCustomers: Pull snapshot for Customers
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS TempPullCustomers (
    SessionId CHAR(36) NOT NULL,
    Id CHAR(36) NOT NULL,
    TenantId CHAR(36) NOT NULL,
    Name VARCHAR(255) NOT NULL,
    Email VARCHAR(255) NULL,
    Phone VARCHAR(50) NULL,
    Address TEXT NULL,
    ModifiedAtUtc DATETIME(6) NOT NULL,
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL,
    IsDeleted BOOLEAN NOT NULL,
    PRIMARY KEY (SessionId, Id),
    INDEX IX_TempPullCustomers_PullSession (SessionId)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- TempPullOrders: Pull snapshot for Orders
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS TempPullOrders (
    SessionId CHAR(36) NOT NULL,
    Id CHAR(36) NOT NULL,
    TenantId CHAR(36) NOT NULL,
    CustomerId CHAR(36) NOT NULL,
    OrderNumber VARCHAR(50) NOT NULL,
    TotalAmount DECIMAL(18,2) NOT NULL,
    OrderDate DATETIME(6) NOT NULL,
    Status VARCHAR(50) NOT NULL,
    ModifiedAtUtc DATETIME(6) NOT NULL,
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL,
    IsDeleted BOOLEAN NOT NULL,
    PRIMARY KEY (SessionId, Id),
    INDEX IX_TempPullOrders_PullSession (SessionId)
) ENGINE=InnoDB;

-- ----------------------------------------------------------------------------
-- TempPullOrderItems: Pull snapshot for OrderItems
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS TempPullOrderItems (
    SessionId CHAR(36) NOT NULL,
    Id CHAR(36) NOT NULL,
    OrderId CHAR(36) NOT NULL,
    ProductId CHAR(36) NOT NULL,
    ProductName VARCHAR(255) NOT NULL,
    Quantity INT NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    ModifiedAtUtc DATETIME(6) NOT NULL,
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL,
    IsDeleted BOOLEAN NOT NULL,
    PRIMARY KEY (SessionId, Id),
    INDEX IX_TempPullOrderItems_PullSession (SessionId)
) ENGINE=InnoDB;

-- ============================================================================
-- INITIAL DATA
-- ============================================================================

-- (No initialization needed - SyncSessions.SyncVersion uses AUTO_INCREMENT)

-- ============================================================================
-- USAGE NOTES FOR YOUR OWN SCHEMA
-- ============================================================================
/*
TO CREATE YOUR OWN SYNCED TABLES:

1. BUSINESS TABLE PATTERN:
   
   CREATE TABLE YourTable (
       Id CHAR(36) PRIMARY KEY,
       -- Your business columns here --
       YourColumn VARCHAR(255) NOT NULL,
       
       -- REQUIRED sync columns --
       ModifiedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
       SyncSessionId CHAR(36) NULL,
       ModifiedByUserId VARCHAR(100) NOT NULL DEFAULT 'System',
       IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
       
       -- REQUIRED index --
       INDEX IX_YourTable_Session (SyncSessionId)
   ) ENGINE=InnoDB;

   NOTE: SyncVersion lives ONLY on SyncSessions table (session-based, not record-based).

2. TEMP PUSH TABLE PATTERN:
   
   CREATE TABLE TempPushYourTable (
       SessionId CHAR(36) NOT NULL,
       SequenceNumber INT AUTO_INCREMENT,
       -- Same columns as business table (minus SyncSessionId) --
       Id CHAR(36) NOT NULL,
       YourColumn VARCHAR(255) NOT NULL,
       ModifiedAtUtc DATETIME(6) NULL,
       ModifiedByUserId VARCHAR(100) NOT NULL,
       IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
       PRIMARY KEY (SessionId, SequenceNumber),
       INDEX IX_TempPushYourTable_Session (SessionId)
   ) ENGINE=InnoDB;

3. TEMP PULL TABLE PATTERN:
   
   CREATE TABLE TempPullYourTable (
       SessionId CHAR(36) NOT NULL,
       -- ALL columns from business table --
       Id CHAR(36) NOT NULL,
       YourColumn VARCHAR(255) NOT NULL,
       ModifiedAtUtc DATETIME(6) NOT NULL,
       SyncSessionId CHAR(36) NULL,
       ModifiedByUserId VARCHAR(100) NOT NULL,
       IsDeleted BOOLEAN NOT NULL,
       PRIMARY KEY (SessionId, Id),
       INDEX IX_TempPullYourTable_PullSession (SessionId)
   ) ENGINE=InnoDB;

   NOTE: Pull tables snapshot business data; no SyncVersion on records.

4. CONFIGURE IN appsettings.json:
   
   "SyncConfiguration": {
     "Tables": {
       "YourTable": {
         "Priority": 1,
         "Enabled": true
       }
     }
   }

FOREIGN KEYS:
- Set Priority in config to match dependency order
- Parent tables = lower priority number (e.g., Customers = 1)
- Child tables = higher priority number (e.g., Orders = 2)
*/
