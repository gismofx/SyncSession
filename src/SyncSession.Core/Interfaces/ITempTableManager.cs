using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SyncSession.Core.DTOs;
using SyncSession.Core.Models;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Interface for managing temporary tables used during push and pull sync operations.
/// Abstracts the hybrid shared/dedicated temp table strategy.
/// </summary>
public interface ITempTableManager
{
    /// <summary>
    /// Determines the temp table strategy for a push operation and returns table info.
    /// Creates a dedicated table if estimated record count exceeds the push threshold;
    /// otherwise returns the shared push table name.
    /// </summary>
    Task<TempTableInfo> GetTempTableForPushAsync(
        Guid sessionId, string tableName, int estimatedRecordCount);

    /// <summary>
    /// Determines the temp table strategy for a pull operation and returns table info.
    /// Creates a dedicated table if estimated record count exceeds the pull threshold;
    /// otherwise returns the shared pull table name.
    /// </summary>
    Task<TempTableInfo> GetTempTableForPullAsync(
        Guid pullSessionId, string tableName, int estimatedRecordCount);

    /// <summary>
    /// Inserts a batch of records into the temp table for a session.
    /// </summary>
    /// <param name="sessionId">Session ID owning the temp table.</param>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <param name="records">Records as key-value dictionaries.</param>
    /// <returns>Number of records inserted.</returns>
    Task<int> InsertBatchAsync(
        Guid sessionId, string tableName, IEnumerable<Dictionary<string, object?>> records);

    /// <summary>
    /// Gets a paginated batch of records from a pull temp table.
    /// Auto-detects shared vs dedicated table based on pull session ID.
    /// </summary>
    /// <param name="pullSessionId">Pull session ID.</param>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <param name="offset">Number of records to skip.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <returns>Batch result containing records, HasMore flag, and total count.</returns>
    Task<PullBatchResult> GetPullBatchAsync(
        Guid pullSessionId, string tableName, int offset, int limit);

    /// <summary>
    /// Cleans up temp tables associated with a completed or failed pull session.
    /// Deletes rows from shared tables and drops dedicated tables.
    /// </summary>
    /// <param name="pullSessionId">Pull session ID to clean up.</param>
    /// <param name="tables">Table metadata identifying which temp tables to clean.</param>
    Task CleanupPullSessionAsync(
        Guid pullSessionId, IEnumerable<SyncSessionTableMetadata> tables);

    /// <summary>
    /// Cleans up all temp tables associated with a specific push session.
    /// Deletes rows from shared tables and drops dedicated tables.
    /// </summary>
    /// <param name="sessionId">Session ID whose temp tables should be cleaned up.</param>
    Task CleanupSessionTablesAsync(Guid sessionId);
}
