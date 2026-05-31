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
/// Finds and drops orphaned dedicated temp tables left behind by incomplete sync sessions.
/// </summary>
/// <remarks>
/// Dedicated tables are created for large syncs and should be dropped after session completion.
/// Orphan age threshold is controlled by <see cref="ServerSyncConfiguration.OrphanedTableCleanupDays"/>.
/// Implements <see cref="ICleanupService"/> for integration with the background cleanup host.
/// </remarks>
public class TempTableCleanupService : ICleanupService
{
    private readonly IServerDatabase _database;
    private readonly ILogger<TempTableCleanupService> _logger;
    private readonly ServerSyncConfiguration _config;

    public TempTableCleanupService(
        IServerDatabase database,
        ILogger<TempTableCleanupService> logger,
        ServerSyncConfiguration config)
    {
        _database = database;
        _logger = logger;
        _config = config;
    }

    /// <inheritdoc/>
    public async Task<int> ExecuteCleanupAsync()
    {
        var droppedCount = await DropOrphanedTempTables();
        droppedCount += await CleanupOrphanedSeedSnapshotsAsync();

        // Log stats as part of the cleanup cycle (previously in background service)
        var stats = await GetTempTableStatistics();
        _logger.LogDebug("Temp table stats: {TempTableStats}", stats);

        return droppedCount;
    }

    /// <inheritdoc/>
    public string GetCleanupDescription() =>
        "Temp table cleanup (orphaned dedicated tables)";

    /// <summary>
    /// Finds all dedicated temp tables currently in the database.
    /// </summary>
    public virtual async Task<List<string>> FindDedicatedTempTables()
    {
        return await _database.FindDedicatedTempTablesAsync();
    }

    /// <summary>
    /// Finds dedicated temp tables not associated with any active session.
    /// </summary>
    public virtual async Task<List<string>> FindOrphanedTempTables()
    {
        var allDedicatedTables = await _database.FindDedicatedTempTablesAsync();

        if (!allDedicatedTables.Any())
        {
            return new List<string>();
        }

        // Get temp tables that ARE associated with active sessions
        var activeTempTables = await _database.FindActiveTempTableNamesAsync();

        var activeSet = new HashSet<string>(activeTempTables, StringComparer.OrdinalIgnoreCase);

        // Orphaned = exists in database but NOT in active sessions
        var orphaned = allDedicatedTables
            .Where(table => !activeSet.Contains(table))
            .ToList();

        return orphaned;
    }

    /// <summary>
    /// Drops all orphaned dedicated temp tables.
    /// </summary>
    /// <returns>The number of tables successfully dropped.</returns>
    public virtual async Task<int> DropOrphanedTempTables()
    {
        var orphanedTables = await FindOrphanedTempTables();

        if (!orphanedTables.Any())
        {
            _logger.LogDebug("No orphaned temp tables found");
            return 0;
        }

        _logger.LogWarning("Found {Count} orphaned temp tables to drop", orphanedTables.Count);

        int droppedCount = 0;

        foreach (var tableName in orphanedTables)
        {
            try
            {
                await _database.DropTempTableAsync(tableName);
                droppedCount++;
                _logger.LogInformation("Dropped orphaned table: {TableName}", tableName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to drop orphaned table {TableName}", tableName);
            }
        }

        return droppedCount;
    }

    /// <summary>
    /// Collects row count statistics across all shared and dedicated temp tables.
    /// </summary>
    /// <returns>A <see cref="TempTableStatistics"/> snapshot of current temp table usage.</returns>
    internal virtual async Task<TempTableStatistics> GetTempTableStatistics()
    {
        var stats = new TempTableStatistics();

        // Count dedicated tables
        var dedicatedTables = await _database.FindDedicatedTempTablesAsync();
        stats.TotalDedicatedTables = dedicatedTables.Count;

        // Count orphaned tables
        var orphanedTables = await FindOrphanedTempTables();
        stats.OrphanedTables = orphanedTables.Count;

        // Get row counts from shared tables (derived from configuration, not hardcoded)
        var sharedTables = _config.GetTables()
            .Where(t => t.Enabled)
            .SelectMany(t => new[] { $"TempPush{t.TableName}", $"TempPull{t.TableName}" });

        foreach (var tableName in sharedTables)
        {
            var rowCount = await _database.CountSharedTempTableRowsAsync(tableName);
            stats.SharedTableRowCounts[tableName] = rowCount;
        }

        return stats;
    }

    /// <summary>
    /// Drops snapshot tables and tracking rows for seed operations that are either Failed
    /// or Active but have exceeded <see cref="ServerSyncConfiguration.SeedSnapshotOrphanHours"/>.
    /// </summary>
    /// <returns>Number of snapshot table sets dropped.</returns>
    public virtual async Task<int> CleanupOrphanedSeedSnapshotsAsync()
    {
        var cutoff = DateTime.UtcNow.AddHours(-_config.SeedSnapshotOrphanHours);
        IEnumerable<Core.Models.SeedSnapshot> orphans;
        try
        {
            orphans = await _database.FindOrphanedSeedSnapshotsAsync(cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query orphaned seed snapshots");
            return 0;
        }

        var orphanList = orphans.ToList();
        if (!orphanList.Any())
        {
            _logger.LogDebug("No orphaned seed snapshots found");
            return 0;
        }

        _logger.LogWarning("Found {Count} orphaned seed snapshot(s) to clean up", orphanList.Count);

        int cleaned = 0;
        foreach (var snapshot in orphanList)
        {
            try
            {
                var tables = await _database.FindSeedSnapshotTableNamesAsync(snapshot.SeedId);
                foreach (var tbl in tables)
                {
                    await _database.DropSeedSnapshotTableAsync(tbl);
                    _logger.LogInformation(
                        "Dropped orphaned seed snapshot table {Table} (seedId={SeedId})",
                        tbl, snapshot.SeedId);
                }
                await _database.DeleteSeedSnapshotAsync(snapshot.SeedId);
                _logger.LogInformation(
                    "Cleaned orphaned seed snapshot seedId={SeedId} status={Status}",
                    snapshot.SeedId, snapshot.Status);
                cleaned++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to clean seed snapshot seedId={SeedId}", snapshot.SeedId);
            }
        }

        return cleaned;
    }
}

/// <summary>
/// Represents a snapshot of current temp table usage for monitoring and diagnostics.
/// </summary>
internal class TempTableStatistics
{
    /// <summary>
    /// Gets or sets the total number of dedicated temp tables in the database.
    /// </summary>
    /// <value>Count of all tables matching the dedicated naming pattern.</value>
    public int TotalDedicatedTables { get; set; }

    /// <summary>
    /// Gets or sets the number of dedicated temp tables not linked to any active session.
    /// </summary>
    /// <value>Count of orphaned tables eligible for cleanup.</value>
    public int OrphanedTables { get; set; }

    /// <summary>
    /// Gets or sets the row counts for each shared temp table.
    /// </summary>
    /// <value>A dictionary mapping shared table names to their current row counts.</value>
    public Dictionary<string, int> SharedTableRowCounts { get; set; } = new();

    public override string ToString()
    {
        var sharedTotal = SharedTableRowCounts.Values.Sum();
        return $"Dedicated: {TotalDedicatedTables} (Orphaned: {OrphanedTables}), " +
               $"Shared rows: {sharedTotal}";
    }
}
