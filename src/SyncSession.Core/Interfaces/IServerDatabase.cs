using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using SyncSession.Core.DTOs;
using SyncSession.Core.Models;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Defines server-side database operations for synchronization infrastructure.
/// </summary>
/// <remarks>
/// Provides a database-agnostic abstraction over session tracking, temp table management,
/// and record upsert operations. Table names are resolved automatically from
/// <see cref="SyncSystem.Core.Attributes.SyncTableAttribute"/> on entity types.
/// </remarks>
public interface IServerDatabase
{
    #region Connection Management
    
    /// <summary>
    /// Gets a database connection for executing queries.
    /// </summary>
    /// <returns>An open database connection.</returns>
    /// <remarks>
    /// Implementations should return pooled connections where appropriate.
    /// Connection is typically scoped to a single operation.
    /// </remarks>
    Task<IDbConnection> GetConnectionAsync();
    
    /// <summary>
    /// Executes multiple database operations within a single transaction.
    /// </summary>
    /// <param name="operations">Lambda containing all operations to execute in transaction</param>
    /// <returns>Task representing the transaction</returns>
    /// <remarks>
    /// Uses the isolation level from <see cref="SyncSystem.Core.Models.SyncConfiguration"/> (default: Serializable).
    /// Transaction automatically commits on success, rolls back on exception and re-throws.
    /// Do not nest <see cref="ExecuteInTransactionAsync"/> calls — nested transactions are not supported.
    /// Serializable isolation prevents phantom reads during concurrent operations.
    /// </remarks>
    Task ExecuteInTransactionAsync(Func<IDbTransaction, Task> operations);
    
    #endregion
    
    #region Session Tracking
    
    /// <summary>
    /// Finds session IDs that a specific device has not yet processed.
    /// </summary>
    /// <param name="deviceId">The device ID to check.</param>
    /// <returns>List of unprocessed session IDs ordered by SyncVersion.</returns>
    /// <remarks>
    /// Core of session-based tracking - prevents "lost records" problem.
    /// Excludes sessions already in ClientProcessedSessions for this device.
    /// </remarks>
    Task<IEnumerable<Guid>> FindUnseenSessionIdsAsync(Guid deviceId);
    
    /// <summary>
    /// Marks sessions as processed by a device.
    /// </summary>
    /// <param name="deviceId">The device that processed the sessions.</param>
    /// <param name="sessionIds">Session IDs that were successfully processed.</param>
    /// <remarks>
    /// Called after a device successfully applies pulled records, and at push-session commit for
    /// the pushing device itself (which already holds those records and must not pull them back).
    /// Inserts records into ClientProcessedSessions table. Idempotent — re-marking is a no-op.
    /// When marking at commit, pass the commit <paramref name="transaction"/> so that a rolled-back
    /// session never leaves a device recorded as having seen records it never received.
    /// </remarks>
    Task MarkSessionsProcessedAsync(Guid deviceId, IEnumerable<Guid> sessionIds, IDbTransaction? transaction = null);

    /// <summary>
    /// Marks all currently-committed sessions as processed for a device after a seed operation.
    /// </summary>
    /// <param name="deviceId">The device that completed the seed.</param>
    /// <param name="tenantId">The tenant that was seeded. Only sessions belonging to this tenant are marked processed.</param>
    /// <remarks>
    /// Bulk-inserts a row into ClientProcessedSessions for every Committed session for the tenant,
    /// using INSERT IGNORE / INSERT OR IGNORE to skip any that already exist.
    /// After this call, a normal pull will return only sessions committed after the seed completed.
    /// These rows are cleaned up automatically by the standard session retention purge.
    /// </remarks>
    Task AcknowledgeSeedAsync(Guid deviceId, Guid tenantId);
    
    #endregion
    
    #region Temp Table Management
    
    /// <summary>
    /// Creates a dedicated temporary table for a session.
    /// </summary>
    /// <param name="tempTableName">Name of temp table to create (e.g., "TempPush_Customers_abc123").</param>
    /// <param name="sourceTableName">Source table to copy structure from.</param>
    /// <remarks>
    /// Used for large syncs (>10K records) to avoid shared table contention.
    /// Table includes SequenceNumber column for ordering.
    /// </remarks>
    Task CreateDedicatedTempTableAsync(string tempTableName, string sourceTableName);
    
    /// <summary>
    /// Drops a temporary table.
    /// </summary>
    /// <param name="tempTableName">Name of temp table to drop.</param>
    /// <remarks>
    /// Safe to call even if table doesn't exist (uses DROP TABLE IF EXISTS).
    /// Used during session cleanup.
    /// </remarks>
    Task DropTempTableAsync(string tempTableName);
    
    /// <summary>
    /// Checks if a temporary table exists.
    /// </summary>
    /// <param name="tempTableName">Name of temp table to check.</param>
    /// <returns>True if table exists, false otherwise.</returns>
    Task<bool> TempTableExistsAsync(string tempTableName);
    
    #endregion
    
    #region Generic Type-Safe Operations

    /// <summary>
    /// Gets records from specific sessions for a table.
    /// </summary>
    /// <typeparam name="T">Entity type implementing ISyncEntity.</typeparam>
    /// <param name="sessionIds">Session IDs to retrieve records from.</param>
    /// <param name="tenantId">Optional tenant filter; applied when entity implements <see cref="IMultiTenantSyncEntity"/>.</param>
    /// <returns>Records associated with the specified sessions.</returns>
    /// <remarks>
    /// Table name extracted from [SyncTable] attribute.
    /// Filters by <paramref name="tenantId"/> if entity implements <see cref="IMultiTenantSyncEntity"/>.
    /// Used during pull operations to get records for client.
    /// </remarks>
    Task<IEnumerable<T>> GetRecordsFromSessionsAsync<T>(IEnumerable<Guid> sessionIds, Guid? tenantId = null) where T : ISyncEntity;
    
    #endregion

    #region Session Management
    
    /// <summary>
    /// Creates a new sync session.
    /// </summary>
    /// <param name="session">Session object to create.</param>
    /// <remarks>
    /// Inserts into SyncSessions table.
    /// SyncVersion is assigned automatically by database (AUTO_INCREMENT).
    /// </remarks>
    Task CreateSessionAsync(SessionRecord session, IDbTransaction? transaction = null);
    
    #endregion
    
    #region Cleanup Operations
    
    /// <summary>
    /// Finds sessions that have been inactive for too long.
    /// </summary>
    /// <param name="cutoffTime">Sessions with LastActivityUtc before this are considered stale.</param>
    /// <returns>List of stale sessions ordered by LastActivityUtc.</returns>
    /// <remarks>
    /// Only returns sessions in Staging, Ready, or Processing status.
    /// Used by SessionCleanupService to mark timed-out sessions as Failed.
    /// </remarks>
    Task<List<SessionRecord>> FindStaleSessionsAsync(DateTime cutoffTime);
    
    /// <summary>
    /// Marks a session as failed with an error message.
    /// </summary>
    /// <param name="sessionId">Session to mark as failed.</param>
    /// <param name="errorMessage">Error message describing why session failed.</param>
    /// <remarks>
    /// Sets Status = 'Failed' and updates ErrorMessage.
    /// Used for timeout and error handling.
    /// </remarks>
    Task MarkSessionFailedAsync(Guid sessionId, string errorMessage);
    
    /// <summary>
    /// Gets all temp tables associated with a session.
    /// </summary>
    /// <param name="sessionId">Session ID to query.</param>
    /// <returns>List of temp table information (name and shared/dedicated strategy).</returns>
    /// <remarks>
    /// Queries SyncSessionTables for the specified session.
    /// Used during cleanup to determine which tables to clean.
    /// </remarks>
    Task<List<TempTableInfo>> GetSessionTempTableInfoAsync(Guid sessionId);
    
    /// <summary>
    /// Deletes records from a shared temp table for a specific session.
    /// </summary>
    /// <param name="tableName">Shared temp table name (e.g., "TempPushCustomers").</param>
    /// <param name="sessionId">Session ID to delete records for.</param>
    /// <returns>Number of rows deleted.</returns>
    /// <remarks>
    /// Only deletes rows matching the SessionId.
    /// Used for cleaning shared tables after session completion.
    /// </remarks>
    Task<int> DeleteFromSharedTempTableAsync(string tableName, Guid sessionId);
    
    /// <summary>
    /// Deletes SyncSessionTables entries for a session.
    /// </summary>
    /// <param name="sessionId">Session ID to delete entries for.</param>
    /// <remarks>
    /// Should be called before deleting the session itself (foreign key).
    /// Part of session cleanup process.
    /// </remarks>
    Task DeleteSessionTablesAsync(Guid sessionId);
    
    /// <summary>
    /// Finds old sessions that can be purged.
    /// </summary>
    /// <param name="cutoffDate">Sessions created before this date are considered old.</param>
    /// <param name="statuses">Only include sessions with these statuses (e.g., "Committed", "Failed").</param>
    /// <returns>List of session IDs that can be deleted.</returns>
    /// <remarks>
    /// Used by SessionCleanupService to purge old completed sessions.
    /// Typically purges sessions older than 30 days.
    /// </remarks>
    Task<List<Guid>> FindOldSessionsAsync(DateTime cutoffDate, string[] statuses);
    
    /// <summary>
    /// Deletes ClientProcessedSessions entries for specified sessions.
    /// </summary>
    /// <param name="sessionIds">Session IDs to delete entries for.</param>
    /// <remarks>
    /// Should be called before deleting sessions themselves (foreign key).
    /// Part of session purge process.
    /// </remarks>
    Task DeleteClientProcessedSessionsAsync(IEnumerable<Guid> sessionIds);
    
    /// <summary>
    /// Deletes sessions and their associated SyncSessionTables entries.
    /// </summary>
    /// <param name="sessionIds">Session IDs to delete.</param>
    /// <remarks>
    /// Deletes SyncSessionTables first, then SyncSessions (foreign key order).
    /// Should call DeleteClientProcessedSessionsAsync first.
    /// </remarks>
    Task DeleteSessionsAsync(IEnumerable<Guid> sessionIds);
    
    /// <summary>
    /// Deletes old records from a shared temp table.
    /// </summary>
    /// <param name="tableName">Shared temp table name.</param>
    /// <param name="cutoffTime">Delete records created before this time.</param>
    /// <returns>Number of rows deleted.</returns>
    /// <remarks>
    /// Used by SharedTableCleanupService to clean up stale temp data.
    /// Safe to call even if table doesn't exist (returns 0).
    /// </remarks>
    Task<int> DeleteOldSharedTempRowsAsync(string tableName, DateTime cutoffTime);
    
    /// <summary>
    /// Finds all dedicated temp tables in the database.
    /// </summary>
    /// <returns>List of temp table names matching dedicated pattern (TempPush_*, TempPull_*).</returns>
    /// <remarks>
    /// Used to identify orphaned dedicated tables for cleanup.
    /// Excludes shared tables like TempPushCustomers.
    /// </remarks>
    Task<List<string>> FindDedicatedTempTablesAsync();
    
    /// <summary>
    /// Finds temp table names that are currently in use by active sessions.
    /// </summary>
    /// <returns>List of temp table names from SyncSessionTables where session is active.</returns>
    /// <remarks>
    /// Active = Status in ('Staging', 'Ready', 'Processing').
    /// Used to determine which dedicated tables should NOT be dropped.
    /// </remarks>
    Task<List<string>> FindActiveTempTableNamesAsync();
    
    /// <summary>
    /// Gets row count for a shared temp table.
    /// </summary>
    /// <param name="tableName">Shared temp table name.</param>
    /// <returns>Number of rows in the table, or 0 if table doesn't exist.</returns>
    /// <remarks>
    /// Used for monitoring and cleanup decisions.
    /// Safe to call even if table doesn't exist.
    /// </remarks>
    Task<int> CountSharedTempTableRowsAsync(string tableName);
    
    #endregion
    
    #region Session Lifecycle Operations
    
    /// <summary>
    /// Inserts a SyncSessionTables entry tracking a table for a session.
    /// </summary>
    /// <param name="sessionId">Session ID this table belongs to.</param>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <param name="tempTableName">Temp table name where data is staged.</param>
    /// <param name="priority">Processing priority (lower = earlier).</param>
    /// <param name="usesSharedTable">True if using shared table, false for dedicated.</param>
    /// <param name="estimatedRecordCount">Estimated number of records for this table (used for strategy decisions and monitoring).</param>
    /// <remarks>
    /// Initial Status is set to 'Staging'.
    /// Called during push session creation for each table.
    /// </remarks>
    Task InsertSessionTableAsync(Guid sessionId, string tableName, string tempTableName, int priority, bool usesSharedTable, int estimatedRecordCount);
    
    /// <summary>
    /// Gets a sync session by ID.
    /// </summary>
    /// <param name="sessionId">Session ID to retrieve.</param>
    /// <returns>Session object if found, null otherwise.</returns>
    /// <remarks>
    /// Returns complete session including Status, SyncVersion, timestamps, and ErrorMessage.
    /// </remarks>
    Task<SessionRecord?> GetSessionAsync(Guid sessionId);
    
    /// <summary>
    /// Updates the LastActivityUtc timestamp for a session (keep-alive).
    /// </summary>
    /// <param name="sessionId">Session ID to update.</param>
    /// <remarks>
    /// Called periodically during long operations to prevent timeout.
    /// Updates LastActivityUtc to current UTC time.
    /// </remarks>
    Task UpdateSessionActivityAsync(Guid sessionId);
    
    /// <summary>
    /// Marks a session as Ready for background processing.
    /// </summary>
    /// <param name="sessionId">Session ID to mark ready.</param>
    /// <returns>True if session was marked ready, false if session not found or not in Staging status.</returns>
    /// <remarks>
    /// Only succeeds if current Status = 'Staging'.
    /// Sets Status = 'Ready' and updates LastActivityUtc.
    /// After this, SyncQueueProcessor will pick it up.
    /// </remarks>
    Task<bool> MarkSessionReadyAsync(Guid sessionId);
    
    /// <summary>
    /// Checks if a session exists, optionally with a specific status.
    /// </summary>
    /// <param name="sessionId">Session ID to check.</param>
    /// <param name="expectedStatus">If provided, only returns true if session has this status.</param>
    /// <returns>True if session exists (with expected status if provided), false otherwise.</returns>
    /// <remarks>
    /// Used for validation before operations.
    /// If expectedStatus is null, returns true for any status.
    /// </remarks>
    Task<bool> SessionExistsAsync(Guid sessionId, string? expectedStatus = null);
    
    /// <summary>
    /// Gets temp table information for a specific table in a session.
    /// </summary>
    /// <param name="sessionId">Session ID.</param>
    /// <param name="tableName">Business table name.</param>
    /// <returns>Temp table info if found, null if session/table combination not found.</returns>
    /// <remarks>
    /// Queries SyncSessionTables for the session/table combination.
    /// Used to locate where data is staged.
    /// </remarks>
    Task<TempTableInfo?> GetSessionTableInfoAsync(Guid sessionId, string tableName);
    
    /// <summary>
    /// Counts records in a temp table for a session.
    /// </summary>
    /// <param name="tempTableName">Temp table name to count.</param>
    /// <param name="sessionId">Session ID (required if usesSharedTable is true).</param>
    /// <param name="usesSharedTable">True if shared table (filters by SessionId), false for dedicated.</param>
    /// <returns>Number of records.</returns>
    /// <remarks>
    /// For shared tables, counts WHERE SessionId = @SessionId.
    /// For dedicated tables, counts all records.
    /// Used to verify batch upload counts.
    /// </remarks>
    Task<int> CountTempTableRecordsAsync(string tempTableName, Guid? sessionId, bool usesSharedTable);
    
    /// <summary>
    /// Updates SyncSessionTables with actual record count and status.
    /// </summary>
    /// <param name="sessionId">Session ID.</param>
    /// <param name="tableName">Business table name.</param>
    /// <param name="actualRecordCount">Actual number of records uploaded.</param>
    /// <param name="status">New status (typically "Ready").</param>
    /// <remarks>
    /// Called after table upload is complete.
    /// Sets ActualRecordCount and Status in SyncSessionTables.
    /// </remarks>
    Task UpdateSessionTableStatusAsync(Guid sessionId, string tableName, int actualRecordCount, string status);
    
    #endregion
    
    #region Temp Table Operations
    
    /// <summary>
    /// Inserts a batch of records into a temp table using dynamic column mapping.
    /// </summary>
    /// <param name="tempTableName">Name of the temp table to insert into.</param>
    /// <param name="usesSharedTable">True if shared table (adds SessionId, CreatedAtUtc columns).</param>
    /// <param name="sessionId">Session ID (used for shared tables).</param>
    /// <param name="tableName">Business table name (e.g., "Customers") for column validation.</param>
    /// <param name="records">List of records as key-value dictionaries.</param>
    /// <returns>Number of records inserted.</returns>
    /// <remarks>
    /// Builds the INSERT statement dynamically from dictionary keys intersected with
    /// valid push columns for <paramref name="tableName"/>. Extra keys (e.g., <c>IsDirty</c>,
    /// <c>SyncSessionId</c>) are silently dropped. For shared tables, automatically adds
    /// <c>SessionId</c> and <c>CreatedAtUtc</c>. All records in the batch must have the same keys.
    /// </remarks>
    Task<int> InsertBatchIntoTempTableAsync(
        string tempTableName,
        bool usesSharedTable,
        Guid sessionId,
        string tableName,
        List<Dictionary<string, object?>> records);
    
    /// <summary>
    /// Gets a paginated batch of records from a pull temp table.
    /// </summary>
    /// <param name="tempTableName">Pull temp table name (e.g., "TempPullCustomers").</param>
    /// <param name="pullSessionId">Pull session ID to filter by.</param>
    /// <param name="offset">Number of records to skip.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <returns>Pull batch result containing records, HasMore flag, and total count.</returns>
    /// <remarks>
    /// Returns records as Dictionary&lt;string, object?&gt; for type flexibility.
    /// Excludes PullSessionId and CreatedAtUtc metadata columns from results.
    /// Orders by Id for consistency.
    /// HasMore = true if there are more records after this batch.
    /// </remarks>
    Task<PullBatchResult> GetPullBatchAsync(
        string tempTableName,
        Guid pullSessionId,
        int offset,
        int limit);
    
    /// <summary>
    /// Deletes pull session data from multiple temp tables.
    /// </summary>
    /// <param name="pullSessionId">Pull session ID to clean up.</param>
    /// <param name="tableNames">Array of temp table names to delete from.</param>
    /// <returns>Total number of rows deleted across all tables.</returns>
    /// <remarks>
    /// Deletes WHERE SessionId = @SessionId from each table.
    /// Safe to call with non-existent tables (they're skipped).
    /// Used after client confirms successful pull.
    /// </remarks>
    Task<int> DeletePullSessionDataAsync(Guid pullSessionId, string[] tableNames);
    
    /// <summary>
    /// Snapshots records from specified sessions into a temp pull table.
    /// </summary>
    /// <param name="tempTableName">Pull temp table name (e.g., "TempPullCustomers" or "TempPull_Customers_abc123").</param>
    /// <param name="sourceTableName">Source business table name (e.g., "Customers").</param>
    /// <param name="sessionIds">Session IDs to snapshot records from.</param>
    /// <param name="pullSessionId">Pull session ID to tag records with.</param>
    /// <param name="usesSharedTable">True if using shared table (includes SessionId column), false for dedicated.</param>
    /// <returns>Number of records copied to temp pull table.</returns>
    /// <remarks>
    /// Copies records WHERE SyncSessionId IN (sessionIds) from source table.
    /// Inserts into temp pull table with SessionId column for tracking.
    /// Used during pull session creation to prepare data for client.
    /// For shared tables, includes SessionId in INSERT; dedicated tables may omit it.
    /// </remarks>
    Task<int> SnapshotRecordsForPullAsync(
        string tempTableName,
        string sourceTableName,
        IEnumerable<Guid> sessionIds,
        Guid pullSessionId,
        bool usesSharedTable,
        Guid? tenantId = null);
    
    #endregion
    
    #region Direct Write Operations
    
    /// <summary>
    /// Directly upsert records to a business table without temp table overhead.
    /// Optimized for small batches (1-1000 records) from direct write operations.
    /// Sets ModifiedAtUtc and SyncSessionId automatically on all records.
    /// </summary>
    /// <param name="tableName">Business table name (e.g., "Customers", "Orders")</param>
    /// <param name="records">List of entities to upsert. All must implement ISyncEntity and be of the same type.</param>
    /// <param name="sessionId">Session ID to assign to all records for sync tracking</param>
    /// <returns>Number of records successfully written to the database</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when tableName is invalid, records list is empty, or entities are of incompatible types
    /// </exception>
    /// <remarks>
    /// This method is designed for direct write operations from web applications and APIs.
    /// It bypasses the temp table infrastructure used by push operations for better performance
    /// on small batches. For large batches (10K+ records), use the standard push flow instead.
    /// 
    /// The method will:
    /// - Validate all records implement ISyncEntity
    /// - Extract column metadata using EntityReflectionHelper
    /// - Build parameterized INSERT...ON DUPLICATE KEY UPDATE SQL
    /// - Execute in batches of 500 records to avoid parameter limits
    /// - Set ModifiedAtUtc to current UTC time
    /// - Set SyncSessionId to the provided sessionId
    /// 
    /// All upserts occur within the current transaction context if one is active.
    /// </remarks>
    Task<int> UpsertDirectAsync(string tableName, List<object> records, Guid sessionId, IDbTransaction? transaction = null);

    /// <summary>
    /// Soft-deletes a record by setting IsDeleted = 1, updating audit columns, and linking to a sync session.
    /// </summary>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <param name="id">Record ID to soft-delete.</param>
    /// <param name="userId">User performing the delete (written to ModifiedByUserId).</param>
    /// <param name="sessionId">Session ID to assign (written to SyncSessionId).</param>
    /// <param name="tenantId">Optional tenant ID. When provided, appended to WHERE clause so the update
    /// is a no-op for records belonging to a different tenant (rows affected = 0 → caller throws).</param>
    /// <param name="transaction">Active transaction to participate in.</param>
    /// <returns>Number of rows affected (0 = not found or tenant mismatch; 1 = success).</returns>
    Task<int> SoftDeleteDirectAsync(
        string tableName,
        Guid id,
        string userId,
        Guid sessionId,
        Guid? tenantId,
        IDbTransaction? transaction = null);
    
    #endregion
    
    #region Data Query Operations

    /// <summary>
    /// Retrieves a single record by ID from the specified table.
    /// Returns property-name/value pairs excluding sync infrastructure columns (IsDirty, SyncSessionId).
    /// </summary>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <param name="id">Record ID to retrieve.</param>
    /// <param name="tenantId">Optional tenant ID. When provided, appended to WHERE clause
    /// so records belonging to other tenants are not returned.</param>
    /// <returns>Dictionary of column name → value, or <c>null</c> if not found (or tenant mismatch).</returns>
    Task<Dictionary<string, object?>?> GetByIdAsync(string tableName, Guid id, Guid? tenantId = null);

    /// <summary>
    /// Executes a filtered, paginated query against the specified table.
    /// Builds parameterized SQL from <see cref="DataQuery"/> filters, enforces tenant isolation,
    /// and excludes soft-deleted records by default.
    /// </summary>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <param name="query">Query specification with filters, pagination, and ordering.</param>
    /// <param name="tenantId">Optional tenant ID. When provided, automatically added as a
    /// non-overridable WHERE condition for <see cref="IMultiTenantSyncEntity"/> tables.</param>
    /// <returns>Result containing matched records, total count, and pagination metadata.</returns>
    /// <exception cref="ArgumentException">Thrown when a filter references an unknown column.</exception>
    Task<DataQueryResult> QueryAsync(string tableName, DataQuery query, Guid? tenantId = null);

    #endregion
    
    #region Queue Processing Operations
    
    /// <summary>
    /// Finds sessions that are ready for processing.
    /// </summary>
    /// <param name="limit">Maximum number of sessions to return (default 100).</param>
    /// <returns>List of sessions with Status = 'Ready' ordered by CreatedAtUtc.</returns>
    /// <remarks>
    /// Used by SyncQueueProcessor to find sessions to process.
    /// Ordered by creation time for FIFO processing.
    /// </remarks>
    Task<List<SessionRecord>> FindReadySessionsAsync(int limit = 100);
    
    /// <summary>
    /// Gets tables associated with a session, ordered by priority.
    /// </summary>
    /// <param name="sessionId">Session ID to query.</param>
    /// <returns>List of session table information ordered by Priority ascending.</returns>
    /// <remarks>
    /// Returns TableName, TempTableName, Priority, and UsesSharedTable.
    /// Used by queue processor to determine processing order (FK dependencies).
    /// </remarks>
    Task<List<SessionTableInfo>> GetSessionTableDetailsAsync(Guid sessionId);
    
    /// <summary>
    /// Upserts data from temp table to main table (transactional).
    /// Called within ExecuteInTransactionAsync during session processing.
    /// </summary>
    /// <param name="tableName">Business table name to upsert into.</param>
    /// <param name="tempTableName">Temp table name to read from.</param>
    /// <param name="usesSharedTable">True if temp table is shared (filters by SessionId).</param>
    /// <param name="sessionId">Session ID - stored in SyncSessionId column for audit trail.</param>
    /// <param name="transaction">Active transaction (REQUIRED)</param>
    /// <returns>Number of rows affected.</returns>
    /// <remarks>
    /// Core operation of queue processing.
    /// For shared tables: filters temp table WHERE SessionId = @SessionId.
    /// Sets ModifiedAtUtc and SyncSessionId on all records.
    /// SyncVersion lives ONLY on SyncSessions table - records link via SyncSessionId.
    /// Uses INSERT ... ON DUPLICATE KEY UPDATE pattern.
    /// MUST be called within transaction - use ExecuteInTransactionAsync.
    /// </remarks>
    Task<int> UpsertFromTempTableAsync(
        string tableName,
        string tempTableName,
        bool usesSharedTable,
        Guid sessionId,
        IDbTransaction transaction);
    
    /// <summary>
    /// Updates a session's status and optional error message.
    /// </summary>
    /// <param name="sessionId">Session ID to update.</param>
    /// <param name="status">New status (Ready, Processing, Committed, Failed).</param>
    /// <param name="transaction">Optional transaction. If null, creates new connection (non-transactional).</param>
    /// <param name="syncVersion">Optional sync version to stamp on the session when committing.</param>
    /// <param name="errorMessage">Optional error message (for Failed status).</param>
    /// <param name="totalRows">Optional total row count across all tables processed in the session.</param>
    /// <param name="rowCountsJson">Optional JSON string recording per-table row counts for monitoring.</param>
    /// <remarks>
    /// Updates Status, LastActivityUtc always.
    /// Sets CommittedAtUtc when status is 'Committed'.
    /// Sets ErrorMessage when provided.
    /// If transaction is null, executes as standalone operation with new connection.
    /// If transaction is provided, executes within that transaction (use with ExecuteInTransactionAsync).
    /// </remarks>
    Task UpdateSessionStatusAsync(
        Guid sessionId,
        string status,
        IDbTransaction? transaction = null,
        long? syncVersion = null,
        string? errorMessage = null,
        int? totalRows = null,
        string? rowCountsJson = null);
    
    /// <summary>
    /// Counts records that would be pulled from specified sessions.
    /// </summary>
    /// <param name="tableName">Business table name to count from.</param>
    /// <param name="sessionIds">Session IDs to count records from.</param>
    /// <returns>Number of records with SyncSessionId IN (sessionIds).</returns>
    /// <remarks>
    /// Used during pull session creation to determine temp table strategy (shared vs dedicated).
    /// Counts WHERE SyncSessionId IN @SessionIds from the main table.
    /// Does not actually pull data - just counts for strategy decision.
    /// </remarks>
    Task<int> CountRecordsFromSessionsAsync(string tableName, IEnumerable<Guid> sessionIds, Guid? tenantId = null);
    
    #endregion

    #region Monitoring

    /// <summary>
    /// Returns recent committed sync sessions for monitoring and audit purposes.
    /// </summary>
    /// <param name="tenantId">Tenant to filter by, or <c>null</c> to return all sessions.</param>
    /// <param name="limit">Maximum number of sessions to return, ordered by most recent first.</param>
    /// <returns>List of session summaries including per-table record counts.</returns>
    /// <remarks>
    /// SECURITY: Callers must validate the requester's identity before passing <paramref name="tenantId"/>.
    /// Passing an arbitrary tenant ID without auth allows cross-tenant data exposure.
    /// </remarks>
    Task<IReadOnlyList<SyncSessionSummary>> GetRecentSessionsAsync(Guid? tenantId, int limit = 50);

    /// <summary>
    /// Returns per-table metadata for a specific sync session.
    /// </summary>
    /// <param name="sessionId">The session to query.</param>
    /// <returns>
    /// List of <see cref="SyncSessionTable"/> entries — one per table included in the session.
    /// Returns an empty list if the session has no table entries (e.g., session not yet committed).
    /// </returns>
    /// <remarks>
    /// Useful for integration test assertions on <c>ActualRecordCount</c> after a push commit,
    /// and for production monitoring dashboards showing per-table sync detail.
    /// </remarks>
    Task<IReadOnlyList<SyncSessionTable>> GetSessionTablesAsync(Guid sessionId);

    #endregion

    #region Schema Maintenance

    /// <summary>
    /// Ensures shared temp tables (<c>TempPush{Table}</c> and <c>TempPull{Table}</c>) exist for
    /// every registered entity table, and adds any missing columns to existing temp tables
    /// to correct schema drift after entity table changes.
    /// </summary>
    /// <remarks>
    /// Called by <c>UseSyncSession()</c> on startup after <c>AutoMigrate</c> and before schema
    /// validation. Prevents "table doesn't exist" errors on first push/pull for a fresh deployment,
    /// and keeps temp tables in sync when entity columns are added or changed.
    /// <para>
    /// Push tables include: <c>SequenceNumber</c>, <c>SessionId</c>, and all columns from
    /// <see cref="ITableMetadataCache.GetColumnsForServerUpsert"/>.<br/>
    /// Pull tables include: <c>SessionId</c> and all columns from
    /// <see cref="ITableMetadataCache.GetColumnsForServerSelect"/>.
    /// </para>
    /// </remarks>
    Task EnsureSharedTempTablesAsync();

    #endregion

    #region Utility Operations

    
    /// <summary>
    /// Executes raw SQL command (INSERT, UPDATE, DELETE, etc).
    /// </summary>
    /// <param name="sql">SQL command to execute.</param>
    /// <returns>Number of rows affected.</returns>
    /// <remarks>
    /// Use with caution - no parameter validation or SQL injection protection.
    /// Intended for administrative operations like test cleanup.
    /// </remarks>
    Task<int> ExecuteRawSqlAsync(string sql);

    /// <summary>
    /// Returns the column names present in the specified table.
    /// Returns an empty list if the table does not exist.
    /// </summary>
    /// <param name="tableName">Table name to inspect.</param>
    /// <returns>Column names (case as stored in the database), or empty list if table missing.</returns>
    /// <remarks>
    /// Used by <c>UseSyncSystem()</c> schema validation on startup.
    /// Not intended for use in hot paths.
    /// </remarks>
    Task<IReadOnlyList<string>> GetTableColumnsAsync(string tableName);
    
    #endregion

    #region Seed Snapshots

    /// <summary>
    /// Finds an existing seed snapshot for the given device and tenant.
    /// </summary>
    /// <param name="deviceId">Device that initiated the seed.</param>
    /// <param name="tenantId">Tenant being seeded.</param>
    /// <returns>
    /// The matching <see cref="SyncSession.Core.Models.SeedSnapshot"/>, or <c>null</c> if none exists.
    /// </returns>
    Task<SeedSnapshot?> FindSeedSnapshotAsync(Guid deviceId, Guid tenantId);

    /// <summary>
    /// Inserts a new seed snapshot row with <c>Status = Active</c>.
    /// </summary>
    /// <param name="seedId">Unique identifier for this seed operation (also used as snapshot table suffix).</param>
    /// <param name="deviceId">Device initiating the seed.</param>
    /// <param name="tenantId">Tenant being seeded.</param>
    Task InsertSeedSnapshotAsync(Guid seedId, Guid deviceId, Guid tenantId);

    /// <summary>
    /// Updates <c>LastActivityUtc</c> for an existing seed snapshot to the current UTC time.
    /// </summary>
    /// <param name="seedId">Seed operation to update.</param>
    Task UpdateSeedSnapshotActivityAsync(Guid seedId);

    /// <summary>
    /// Updates the <c>Status</c> column of an existing seed snapshot.
    /// </summary>
    /// <param name="seedId">Seed operation to update.</param>
    /// <param name="status">New status value (use <see cref="SyncSession.Core.Models.SeedSnapshotStatus"/> constants).</param>
    Task UpdateSeedSnapshotStatusAsync(Guid seedId, string status);

    /// <summary>
    /// Deletes a seed snapshot row by its <paramref name="seedId"/>.
    /// </summary>
    /// <param name="seedId">Seed operation to remove.</param>
    Task DeleteSeedSnapshotAsync(Guid seedId);

    /// <summary>
    /// Creates a snapshot table named <paramref name="snapTableName"/> mirroring
    /// <paramref name="sourceTableName"/> and bulk-copies all rows for <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="snapTableName">Name of the snapshot table to create (e.g., <c>SeedSnap_Customers_&lt;seedId&gt;</c>).</param>
    /// <param name="sourceTableName">Business table to copy from.</param>
    /// <param name="tenantId">When non-null, filters rows by <c>TenantId</c> column.
    /// Pass <c>null</c> for non-tenant tables (e.g., Products) to copy all rows.</param>
    /// <remarks>
    /// Creates the table with <c>CREATE TABLE … AS SELECT</c> (MySQL) or
    /// <c>CREATE TABLE … AS SELECT</c> (SQLite) semantics.
    /// The snapshot is isolated from concurrent writes for the duration of the seed stream.
    /// </remarks>
    Task CreateSeedSnapshotTableAsync(string snapTableName, string sourceTableName, Guid? tenantId);

    /// <summary>
    /// Returns all seed snapshot rows whose <c>Status</c> is <c>Active</c> and whose
    /// <c>LastActivityUtc</c> is older than <paramref name="cutoff"/>.
    /// </summary>
    /// <param name="cutoff">UTC cutoff; rows older than this are considered orphaned.</param>
    /// <returns>Sequence of orphaned snapshot records including their <c>SeedId</c>.</returns>
    Task<IEnumerable<SeedSnapshot>> FindOrphanedSeedSnapshotsAsync(DateTime cutoff);

    /// <summary>
    /// Returns the names of all snapshot tables associated with a given <paramref name="seedId"/>
    /// (i.e., tables whose name ends with the seed ID suffix).
    /// Used during cleanup to enumerate tables before dropping them.
    /// </summary>
    Task<List<string>> FindSeedSnapshotTableNamesAsync(Guid seedId);

    /// <summary>
    /// Returns a page of rows from a snapshot table as raw dictionaries.
    /// The snapshot table has no <c>SessionId</c> column; paging is by offset.
    /// </summary>
    /// <param name="snapTableName">Snapshot table to read from.</param>
    /// <param name="offset">Zero-based row offset.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    Task<List<Dictionary<string, object?>>> GetSeedSnapshotBatchAsync(string snapTableName, int offset, int limit);

    /// <summary>
    /// Returns a page of rows from a snapshot table using keyset pagination.
    /// Avoids O(N) OFFSET scans — O(1) regardless of position in the table.
    /// </summary>
    /// <param name="snapTableName">Snapshot table to read from.</param>
    /// <param name="afterId">Return only rows with Id &gt; this value, ordered by Id.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    Task<List<Dictionary<string, object?>>> GetSeedSnapshotBatchAfterIdAsync(string snapTableName, string afterId, int limit);

    /// <summary>
    /// Returns the total row count of a snapshot table.
    /// </summary>
    /// <param name="snapTableName">Snapshot table to count.</param>
    Task<int> GetSeedSnapshotCountAsync(string snapTableName);

    /// <summary>
    /// Drops a seed snapshot table. Safe to call if the table does not exist (IF EXISTS semantics).
    /// </summary>
    /// <param name="snapTableName">Snapshot table name to drop.</param>
    Task DropSeedSnapshotTableAsync(string snapTableName);

    #endregion

}
