using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Server.Models;

namespace SyncSession.Server.Services;

/// <summary>
/// Cleans up orphaned rows from shared temporary tables.
/// </summary>
/// <remarks>
/// Shared temp tables are reused across multiple sessions and can accumulate rows from
/// sessions that completed without proper cleanup. Removes rows older than a configured
/// retention period. Table names are derived from <see cref="SyncConfiguration.Tables"/>
/// registered in <see cref="ServerSyncConfiguration"/>.
/// </remarks>
public class SharedTableCleanupService : ICleanupService
{
    private readonly IServerDatabase _database;
    private readonly ILogger<SharedTableCleanupService> _logger;
    private readonly ServerSyncConfiguration _config;

    public SharedTableCleanupService(
        IServerDatabase database,
        ILogger<SharedTableCleanupService> logger,
        ServerSyncConfiguration config)
    {
        _database = database;
        _logger = logger;
        _config = config;
    }

    /// <inheritdoc/>
    public async Task<int> ExecuteCleanupAsync() =>
        await CleanupSharedTempTablesAsync(olderThanHours: 24);

    /// <inheritdoc/>
    public string GetCleanupDescription() =>
        "Shared table cleanup (orphaned rows older than 24 hours)";

    /// <summary>
    /// Returns all shared temp table names derived from the current configuration.
    /// </summary>
    /// <returns>A list of table names in the form <c>TempPush{TableName}</c> and <c>TempPull{TableName}</c> for each enabled table.</returns>
    private List<string> GetSharedTempTableNames()
    {
        var tableNames = new List<string>();

        foreach (var table in _config.Tables.Where(t => t.Value.Enabled))
        {
            tableNames.Add($"TempPush{table.Key}");
            tableNames.Add($"TempPull{table.Key}");
        }

        return tableNames;
    }

    /// <summary>
    /// Deletes rows older than the specified number of hours from all shared temp tables.
    /// </summary>
    /// <param name="olderThanHours">Age threshold in hours. Rows created before this cutoff are deleted.</param>
    /// <returns>Total number of rows deleted across all shared temp tables.</returns>
    public virtual async Task<int> CleanupSharedTempTablesAsync(int olderThanHours = 24)
    {
        var cutoffTime = DateTime.UtcNow.AddHours(-olderThanHours);
        var sharedTempTables = GetSharedTempTableNames();

        var enabledTables = _config.GetTables().Where(t => t.Enabled);

        _logger.LogInformation("Starting cleanup of {Count} shared temp tables (cutoff: {CutoffTime:yyyy-MM-dd HH:mm:ss} UTC)",
            sharedTempTables.Count, cutoffTime);

        int totalDeleted = 0;

        foreach (var tableName in sharedTempTables)
        {
            try
            {
                var deleted = await _database.DeleteOldSharedTempRowsAsync(tableName, cutoffTime);

                if (deleted > 0)
                {
                    totalDeleted += deleted;
                    _logger.LogInformation("Deleted {Count} old rows from {TableName}", deleted, tableName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup shared temp table {TableName}", tableName);
            }
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation("Cleanup complete: deleted {TotalDeleted} total rows from shared temp tables", totalDeleted);
        }
        else
        {
            _logger.LogDebug("No old rows found to cleanup");
        }

        return totalDeleted;
    }

    /// <summary>
    /// Returns the current row count for each shared temp table.
    /// </summary>
    /// <returns>A dictionary mapping shared table names to their row counts. Returns <c>-1</c> for any table that could not be queried.</returns>
    public virtual async Task<Dictionary<string, int>> GetSharedTempTableRowCountsAsync()
    {
        var sharedTempTables = GetSharedTempTableNames();
        var counts = new Dictionary<string, int>();

        foreach (var tableName in sharedTempTables)
        {
            try
            {
                var count = await _database.CountSharedTempTableRowsAsync(tableName);
                counts[tableName] = count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get row count for {TableName}", tableName);
                counts[tableName] = -1; // Indicate error
            }
        }

        return counts;
    }
}
