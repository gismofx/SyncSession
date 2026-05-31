using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Server.Services;

/// <summary>
/// Produces a streaming NDJSON seed payload using server-side snapshot tables.
/// Snapshot tables are created once per (DeviceId, TenantId) and remain stable
/// for the duration of the stream, preventing paging race conditions on live tables.
/// Retry-safe: an existing Active snapshot is reused if all tables are present;
/// a Failed or incomplete snapshot is rebuilt from scratch.
/// </summary>
public sealed class SeedService : ISeedService
{
    /// <summary>Number of records bundled into a single <c>rows</c> NDJSON line.
    /// Reducing client ReadLineAsync calls proportionally.</summary>
    internal const int RowsBundleSize = 100;

    private readonly IServerDatabase _database;
    private readonly SyncConfiguration _syncConfig;
    private readonly ITableMetadataCache _metadataCache;
    private readonly ILogger<SeedService> _logger;

    public SeedService(
        IServerDatabase database,
        SyncConfiguration syncConfig,
        ITableMetadataCache metadataCache,
        ILogger<SeedService> logger)
    {
        _database = database;
        _syncConfig = syncConfig;
        _metadataCache = metadataCache;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SeedLine> StreamSeedAsync(
        Guid tenantId,
        Guid deviceId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var tables = _syncConfig.GetAllTables()
            .Where(t => t.Enabled)
            .OrderBy(t => t.Priority)
            .ToList();

        var tableNames = tables.Select(t => t.TableName).ToList();
        var anchor = DateTime.UtcNow;

        var seedId = Guid.Empty;
        await foreach (var heartbeat in PrepareSnapshotAsync(deviceId, tenantId, tableNames, ct))
        {
            yield return heartbeat; // forward "preparing" heartbeats to client
            if (heartbeat.Type == "snapshot_ready")
                seedId = Guid.Parse(heartbeat.Table!); // piggyback seedId on last heartbeat
        }

        _logger.LogInformation(
            "Seed stream starting: tenantId={TenantId}, deviceId={DeviceId}, seedId={SeedId}, tables={Count}, anchor={Anchor:O}",
            tenantId, deviceId, seedId, tables.Count, anchor);

        bool success = false;
        try
        {
            yield return SeedLine.Begin(tenantId, anchor, tableNames);

            foreach (var table in tables)
            {
                ct.ThrowIfCancellationRequested();

                var snapTableName = SnapTableName(table.TableName, seedId);
                int total;
                try
                {
                    total = await _database.GetSeedSnapshotCountAsync(snapTableName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Seed: failed to count snapshot table {SnapTable} for tenant {TenantId}",
                        snapTableName, tenantId);
                    throw;
                }

                _logger.LogDebug("Seed: streaming {SnapTable} ({Total} rows)", snapTableName, total);
                yield return SeedLine.TableStart(table.TableName, total);

                var offset = 0;
                string? lastId = null;
                var bundle = new List<Dictionary<string, object?>>(RowsBundleSize);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var pageSize = RowsBundleSize * 5; // fetch 5 bundles at a time
                    List<Dictionary<string, object?>> batch;
                    try
                    {
                        batch = lastId == null
                            ? await _database.GetSeedSnapshotBatchAsync(snapTableName, offset, pageSize)
                            : await _database.GetSeedSnapshotBatchAfterIdAsync(snapTableName, lastId, pageSize);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Seed: query failed on {SnapTable} after {Offset} rows (lastId={LastId}) for tenant {TenantId}",
                            snapTableName, offset, lastId, tenantId);
                        throw;
                    }

                    foreach (var record in batch)
                    {
                        bundle.Add(record);
                        if (bundle.Count >= RowsBundleSize)
                        {
                            yield return SeedLine.Bundle(table.TableName, bundle);
                            bundle = new List<Dictionary<string, object?>>(RowsBundleSize);
                        }
                    }

                    offset += batch.Count;
                    if (batch.Count < pageSize) break;

                    if (batch.Count > 0 && batch[^1].TryGetValue("Id", out var idVal) && idVal != null)
                        lastId = idVal.ToString();
                }

                // Flush remaining rows
                if (bundle.Count > 0)
                    yield return SeedLine.Bundle(table.TableName, bundle);

                _logger.LogDebug("Seed: completed {Table} ({Count} rows)", table.TableName, offset);
                yield return SeedLine.TableEnd(table.TableName);
            }

            _logger.LogInformation("Seed stream complete: tenantId={TenantId}, anchor={Anchor:O}", tenantId, anchor);
            yield return SeedLine.End(anchor);
            success = true;
        }
        finally
        {
            await CleanupSnapshotAsync(seedId, tableNames, success);
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

    private static string SnapTableName(string tableName, Guid seedId)
        => $"SeedSnap_{tableName}_{seedId:N}";

    /// <summary>
    /// Resolves or creates the snapshot for (deviceId, tenantId).
    /// Returns the seedId to use for table name suffixes.
    /// </summary>
    private async IAsyncEnumerable<SeedLine> PrepareSnapshotAsync(
        Guid deviceId, Guid tenantId, List<string> tableNames,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var existing = await _database.FindSeedSnapshotAsync(deviceId, tenantId);

        if (existing != null)
        {
            if (existing.Status == SeedSnapshotStatus.Active)
            {
                var snapTables = await _database.FindSeedSnapshotTableNamesAsync(existing.SeedId);
                var expectedCount = tableNames.Count;
                if (snapTables.Count == expectedCount)
                {
                    _logger.LogInformation(
                        "Seed: reusing existing snapshot seedId={SeedId}", existing.SeedId);
                    await _database.UpdateSeedSnapshotActivityAsync(existing.SeedId);
                    yield return new SeedLine { Type = "snapshot_ready", Table = existing.SeedId.ToString() };
                    yield break;
                }
                _logger.LogWarning(
                    "Seed: Active snapshot seedId={SeedId} has {Found}/{Expected} tables — rebuilding",
                    existing.SeedId, snapTables.Count, expectedCount);
            }
            else
            {
                _logger.LogInformation(
                    "Seed: stale snapshot seedId={SeedId} status={Status} — rebuilding",
                    existing.SeedId, existing.Status);
            }

            await PurgeSnapshotRowAndTablesAsync(existing.SeedId, tableNames);
        }

        var seedId = Guid.NewGuid();
        await _database.InsertSeedSnapshotAsync(seedId, deviceId, tenantId);

        var tableCount = tableNames.Count;
        for (var i = 0; i < tableCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tableName = tableNames[i];
            var snapTableName = SnapTableName(tableName, seedId);
            _logger.LogDebug("Seed: creating snapshot table {SnapTable}", snapTableName);

            yield return SeedLine.Preparing(tableName); // heartbeat — client shows progress

            var tableConfig = _syncConfig.GetAllTables().FirstOrDefault(t => t.TableName == tableName);
            var isTenantFiltered = tableConfig != null &&
                (tableConfig.TenantFiltered ||
                 typeof(IMultiTenantSyncEntity).IsAssignableFrom(tableConfig.EntityType));

            await _database.CreateSeedSnapshotTableAsync(snapTableName, tableName, isTenantFiltered ? tenantId : null);
        }

        yield return new SeedLine { Type = "snapshot_ready", Table = seedId.ToString() };
    }

    private async Task PurgeSnapshotRowAndTablesAsync(Guid seedId, List<string> tableNames)
    {
        // Drop known tables by convention (may not all exist)
        foreach (var tableName in tableNames)
            await _database.DropSeedSnapshotTableAsync(SnapTableName(tableName, seedId));

        // Also drop any unexpected leftovers found via metadata
        var extra = await _database.FindSeedSnapshotTableNamesAsync(seedId);
        foreach (var tbl in extra)
            await _database.DropSeedSnapshotTableAsync(tbl);

        await _database.DeleteSeedSnapshotAsync(seedId);
    }

    private async Task CleanupSnapshotAsync(Guid seedId, List<string> tableNames, bool success)
    {
        try
        {
            if (success)
            {
                // Normal completion: drop tables then remove row
                foreach (var tableName in tableNames)
                    await _database.DropSeedSnapshotTableAsync(SnapTableName(tableName, seedId));
                await _database.DeleteSeedSnapshotAsync(seedId);
                _logger.LogDebug("Seed: snapshot seedId={SeedId} cleaned up after successful stream", seedId);
            }
            else
            {
                // Failure / cancellation: mark Failed so orphan cleanup picks it up
                await _database.UpdateSeedSnapshotStatusAsync(seedId, SeedSnapshotStatus.Failed);
                _logger.LogWarning("Seed: snapshot seedId={SeedId} marked Failed", seedId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seed: cleanup failed for seedId={SeedId}", seedId);
        }
    }
}
