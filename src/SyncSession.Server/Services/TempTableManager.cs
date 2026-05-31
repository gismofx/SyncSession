using System;
using System.Collections.Generic;
using System.Linq;
using SyncSession.Core.DTOs;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Server.Models;

namespace SyncSession.Server.Services;

/// <summary>
/// Manages temp table lifecycle using a hybrid shared/dedicated strategy.
/// </summary>
/// <remarks>
/// Chooses between shared tables (reused across sessions) and dedicated tables
/// (isolated per session) based on estimated record count thresholds in
/// <see cref="ServerSyncConfiguration"/>.
/// </remarks>
internal class TempTableManager : ITempTableManager
{
    private readonly IServerDatabase _database;
    private readonly ServerSyncConfiguration _config;
    private readonly ILogger<TempTableManager> _logger;

    public TempTableManager(
        IServerDatabase database,
        ServerSyncConfiguration config,
        ILogger<TempTableManager> logger)
    {
        _database = database;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<TempTableInfo> GetTempTableForPushAsync(
        Guid sessionId, string tableName, int estimatedRecordCount)
        => GetTempTableCoreAsync(sessionId, tableName, estimatedRecordCount, isPush: true);

    /// <inheritdoc/>
    public Task<TempTableInfo> GetTempTableForPullAsync(
        Guid pullSessionId, string tableName, int estimatedRecordCount)
        => GetTempTableCoreAsync(pullSessionId, tableName, estimatedRecordCount, isPush: false);

    /// <summary>
    /// Shared implementation for push and pull temp table strategy selection.
    /// </summary>
    private async Task<TempTableInfo> GetTempTableCoreAsync(
        Guid sessionId,
        string tableName,
        int estimatedRecordCount,
        bool isPush)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("SessionId cannot be empty", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentNullException(nameof(tableName));
        if (estimatedRecordCount < 0)
            throw new ArgumentException("EstimatedRecordCount cannot be negative", nameof(estimatedRecordCount));

        var threshold = isPush
            ? _config.PushSharedTableThreshold
            : _config.PullSharedTableThreshold;

        if (estimatedRecordCount < threshold)
        {
            var sharedTableName = GetSharedTempTableName(tableName, isPush);

            _logger.LogDebug("Using shared temp table {TempTableName} for session {SessionId} ({RecordCount} records)",
                sharedTableName, sessionId, estimatedRecordCount);

            return new TempTableInfo { TempTableName = sharedTableName, UsesSharedTable = true };
        }
        else
        {
            var dedicatedTableName = GetDedicatedTempTableName(tableName, sessionId, isPush);

            await _database.CreateDedicatedTempTableAsync(dedicatedTableName, tableName);

            _logger.LogInformation("Created dedicated temp table {TempTableName} for session {SessionId} ({RecordCount} records)",
                dedicatedTableName, sessionId, estimatedRecordCount);

            return new TempTableInfo { TempTableName = dedicatedTableName, UsesSharedTable = false };
        }
    }

    /// <inheritdoc/>
    public async Task CleanupSessionTablesAsync(Guid sessionId)
    {
        // Get tables for this session via database abstraction
        var tables = await _database.GetSessionTempTableInfoAsync(sessionId);

        foreach (var table in tables)
        {
            if (table.UsesSharedTable)
            {
                // Delete rows from shared table
                int rowsDeleted = await _database.DeleteFromSharedTempTableAsync(table.TempTableName, sessionId);
                _logger.LogDebug("Deleted {RowCount} row(s) from {TempTableName} for session {SessionId}",
                    rowsDeleted, table.TempTableName, sessionId);
            }
            else
            {
                // Drop dedicated table
                await _database.DropTempTableAsync(table.TempTableName);
            }
        }

        _logger.LogInformation("Cleaned up temp tables for session {SessionId}", sessionId);
    }

    /// <inheritdoc/>
    public async Task<int> InsertBatchAsync(
        Guid sessionId,
        string tableName,
        IEnumerable<Dictionary<string, object?>> records)
    {
        // Materialize the enumerable to avoid multiple enumerations
        var recordsList = records.ToList();
        
        if (!recordsList.Any())
            return 0;

        // Get temp table info via database abstraction
        var tableInfo = await _database.GetSessionTableInfoAsync(sessionId, tableName);

        if (tableInfo == null)
        {
            throw new InvalidOperationException(
                $"Temp table not found for session {sessionId}, table {tableName}");
        }

        // Insert via database abstraction
        var count = await _database.InsertBatchIntoTempTableAsync(
            tableInfo.Value.TempTableName,
            tableInfo.Value.UsesSharedTable,
            sessionId,
            tableName,
            recordsList);

        _logger.LogDebug("Inserted {Count} records into {TempTable} for session {SessionId}",
            count, tableInfo.Value.TempTableName, sessionId);

        return count;
    }

    /// <inheritdoc/>
    public async Task<PullBatchResult> GetPullBatchAsync(
        Guid pullSessionId,
        string tableName,
        int offset,
        int limit)
    {
        // Determine which temp table to use (dedicated vs shared)
        // Dedicated: TempPull_TableName_PullSessionId
        // Shared: TempPullTableName
        var dedicatedTableName = GetDedicatedTempTableName(tableName, pullSessionId, isPush: false);
        var sharedTableName = GetSharedTempTableName(tableName, isPush: false);
        
        // Check if dedicated table exists
        var usesDedicated = await _database.TempTableExistsAsync(dedicatedTableName);
        var tempTableName = usesDedicated ? dedicatedTableName : sharedTableName;
        
        // Get batch via database abstraction
        // If dedicated, pull session ID filtering not needed (table is isolated)
        // If shared, pull session ID filtering is required
        var result = await _database.GetPullBatchAsync(
            tempTableName,
            pullSessionId,
            offset,
            limit);

        _logger.LogDebug(
            "Retrieved {Count} records from {TempTable} ({Strategy}, offset: {Offset}, hasMore: {HasMore})",
            result.Records.Count, tempTableName, usesDedicated ? "dedicated" : "shared", offset, result.HasMore);

        return result;
    }

    /// <inheritdoc/>
    public async Task CleanupPullSessionAsync(Guid pullSessionId, IEnumerable<SyncSessionTableMetadata> tables)
    {
        var totalDeleted = 0;
        var dedicatedTablesDropped = 0;
        
        foreach (var metadata in tables)
        {
            if (metadata.UsesSharedTable)
            {
                // Delete from shared table using exact name from metadata
                var deleted = await _database.DeletePullSessionDataAsync(pullSessionId, new[] { metadata.TempTableName });
                totalDeleted += deleted;
                _logger.LogDebug("Deleted {Count} records from shared table {TempTable}", deleted, metadata.TempTableName);
            }
            else
            {
                // Drop dedicated table using exact name from metadata
                await _database.DropTempTableAsync(metadata.TempTableName);
                dedicatedTablesDropped++;
                _logger.LogDebug("Dropped dedicated pull table {TempTable}", metadata.TempTableName);
            }
        }

        _logger.LogInformation(
            "Cleaned up pull session {PullSessionId}: {DedicatedDropped} dedicated tables dropped, {SharedDeleted} shared records deleted",
            pullSessionId, dedicatedTablesDropped, totalDeleted);
    }
    
    #region Private Helper Methods
    
    /// <summary>
    /// Generates the shared temp table name for a given business table.
    /// </summary>
    private static string GetSharedTempTableName(string tableName, bool isPush)
    {
        return isPush ? $"TempPush{tableName}" : $"TempPull{tableName}";
    }
    
    /// <summary>
    /// Generates the dedicated temp table name for a given business table and session.
    /// </summary>
    private static string GetDedicatedTempTableName(string tableName, Guid sessionId, bool isPush)
    {
        var prefix = isPush ? "TempPush" : "TempPull";
        return $"{prefix}_{tableName}_{sessionId:N}";
    }
    
    #endregion
}
