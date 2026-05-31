using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Client.Engine;

/// <summary>
/// Production-ready client sync engine with pluggable dependencies.
/// Works with any IClientDatabase and ISyncClient implementation.
/// </summary>
public class ClientSyncEngine : ISyncEngine, IDisposable
{
    private readonly IClientDatabase _clientDatabase;
    private readonly ISyncServerApi _serverClient;
    private readonly Guid _deviceId;
    private readonly ClientSyncConfiguration _config;
    private bool _disposed;

    /// <summary>
    /// Internal constructor for <see cref="ClientSyncEngine"/>. Do not call directly.
    /// </summary>
    /// <remarks>
    /// Use <see cref="ClientSyncEngineBuilder.Build"/> to create instances with automatic
    /// table discovery and strongly-typed handler registration.
    /// </remarks>
    internal ClientSyncEngine(
        IClientDatabase clientDatabase,
        ISyncServerApi serverClient,
        Guid deviceId,
        ClientSyncConfiguration configuration)
    {
        _clientDatabase = clientDatabase ?? throw new ArgumentNullException(nameof(clientDatabase));
        _serverClient = serverClient ?? throw new ArgumentNullException(nameof(serverClient));
        _deviceId = deviceId;
        _config = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <inheritdoc />
    public async Task<SyncResult> SynchronizeAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new SyncResult
        {
            StartedAtUtc = DateTime.UtcNow
        };

        try
        {
            var totalTables = _config.GetTables().Count() * 2;
            progress?.Report(new SyncProgress
            {
                Phase = SyncPhase.Connecting,
                TotalTables = totalTables,
                StatusMessage = "Connecting to server..."
            });

            var pushCount = await PushAsync(progress, cancellationToken);
            result.RecordsPushed = pushCount;

            var pullCount = await PullAsync(progress, cancellationToken);
            result.RecordsPulled = pullCount;

            progress?.Report(new SyncProgress
            {
                Phase = SyncPhase.Complete,
                TotalTables = totalTables,
                StatusMessage = $"Sync complete. Pushed: {result.RecordsPushed:N0}, Pulled: {result.RecordsPulled:N0}"
            });

            result.Success = true;
            result.CompletedAtUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new SyncProgress { Phase = SyncPhase.Cancelled });
            result.Success = false;
            result.ErrorMessage = "Sync was cancelled.";
            result.CompletedAtUtc = DateTime.UtcNow;
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.CompletedAtUtc = DateTime.UtcNow;
            throw;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> PushAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tables = _config.GetTables().ToList();
        var totalTables = tables.Count * 2;

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PushBegin,
            TotalTables = totalTables,
            StatusMessage = "Collecting modified record counts..."
        });

        // Collect dirty counts for all tables
        var tableSyncInfo = new List<TableSyncInfo>();
        var dirtyCounts = new Dictionary<string, int>();

        foreach (var table in tables)
        {
            var dirtyCount = await GetDirtyCountAsync(table);
            dirtyCounts[table.TableName] = dirtyCount;
            if (dirtyCount > 0)
            {
                tableSyncInfo.Add(new TableSyncInfo
                {
                    TableName = table.TableName,
                    EstimatedRecordCount = dirtyCount
                });
            }
        }

        if (!tableSyncInfo.Any())
        {
            progress?.Report(new SyncProgress
            {
                Phase = SyncPhase.PushBegin,
                TotalTables = totalTables,
                StatusMessage = "No records to push."
            });
            return 0;
        }

        // Begin push session
        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PushBegin,
            TotalTables = totalTables,
            StatusMessage = "Opening push session..."
        });

        var sessionId = await _serverClient.BeginPushAsync(tableSyncInfo, _config.TenantId, _config.UserDisplayName);

        var totalPushed = 0;
        var tableIndex = 0;

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tableIndex++;

            var dirtyCount = dirtyCounts[table.TableName];

            if (dirtyCount == 0)
            {
                progress?.Report(new SyncProgress
                {
                    Phase = SyncPhase.PushTable,
                    CurrentTable = table.TableName,
                    TablesCompleted = tableIndex,
                    TotalTables = totalTables,
                    RecordsProcessed = 0,
                    TotalRecords = 0,
                    StatusMessage = $"Skipping {table.TableName} — nothing to push."
                });
                continue;
            }

            var pushed = await PushTableAsync(
                table,
                sessionId,
                tableIndex,
                totalTables,
                dirtyCount,
                progress,
                cancellationToken);

            await _serverClient.CompleteTableAsync(sessionId, table.TableName, pushed);

            totalPushed += pushed;
        }

        // Complete push session
        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PushComplete,
            TablesCompleted = tableIndex,
            TotalTables = totalTables,
            StatusMessage = "Finalizing push session..."
        });

        await _serverClient.CompletePushAsync(sessionId);

        await WaitForPushCommitAsync(sessionId, tableIndex, totalTables, progress);

        return totalPushed;
    }

    /// <inheritdoc />
    public async Task<int> PullAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tables = _config.GetTables().ToList();
        var tableCount = tables.Count;
        var totalTables = tableCount * 2;
        var pullStartIndex = tableCount;

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PullBegin,
            TablesCompleted = pullStartIndex,
            TotalTables = totalTables,
            StatusMessage = "Checking for server changes..."
        });

        var response = await _serverClient.BeginPullAsync(
            tables.Select(t => t.TableName).ToList(),
            _config.TenantId,
            _config.UserDisplayName);

        if (response.Tables.All(t => t.Value.TotalRecords == 0))
        {
            await _serverClient.CompletePullAsync(response.PullSessionId, new List<Guid>());
            progress?.Report(new SyncProgress
            {
                Phase = SyncPhase.PullComplete,
                TablesCompleted = pullStartIndex,
                TotalTables = totalTables,
                StatusMessage = "No records to pull."
            });
            return 0;
        }

        var processedSessionIds = new List<Guid>();
        var totalPulled = 0;
        var tableIndex = 0;

        await _clientDatabase.ExecuteInTransactionAsync(async (transaction) =>
        {
            foreach (var table in tables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tableTotal = response.Tables.TryGetValue(table.TableName, out var metadata)
                    ? (metadata.TotalRecords ?? 0)
                    : 0;

                if (tableTotal == 0)
                {
                    progress?.Report(new SyncProgress
                    {
                        Phase = SyncPhase.PullTable,
                        CurrentTable = table.TableName,
                        TablesCompleted = pullStartIndex + tableIndex,
                        TotalTables = totalTables,
                        RecordsProcessed = 0,
                        TotalRecords = 0,
                        StatusMessage = $"Skipping {table.TableName} — no records to pull."
                    });
                    tableIndex++;
                    continue;
                }

                var (pulled, sessionIds) = await PullTableAsync(
                    table,
                    response.PullSessionId,
                    pullStartIndex + tableIndex,
                    totalTables,
                    tableTotal,
                    transaction,
                    progress,
                    cancellationToken);

                totalPulled += pulled;

                foreach (var sid in sessionIds)
                {
                    if (!processedSessionIds.Contains(sid))
                        processedSessionIds.Add(sid);
                }

                tableIndex++;
            }

            progress?.Report(new SyncProgress
            {
                Phase = SyncPhase.PullComplete,
                TablesCompleted = pullStartIndex + tableIndex,
                TotalTables = totalTables,
                StatusMessage = "Committing changes locally..."
            });
        });

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PullComplete,
            TablesCompleted = pullStartIndex + tableIndex,
            TotalTables = totalTables,
            StatusMessage = "Notifying server of completion..."
        });

        await _serverClient.CompletePullAsync(response.PullSessionId, processedSessionIds);

        return totalPulled;
    }

    #region Private Helpers

    /// <summary>
    /// Polls the server until the push session reaches Committed or Failed status.
    /// </summary>
    /// <param name="sessionId">The push session ID to poll.</param>
    /// <param name="completedTableIndex">Number of push tables completed, used to hold overall progress steady during the wait.</param>
    /// <param name="totalTables">Total number of tables in the sync sequence (push + pull), used for progress reporting.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <remarks>
    /// Intentionally non-cancellable. Once <see cref="ISyncServerApi.CompletePushAsync"/> has been
    /// called the session is queued on the server and will be processed regardless of client state.
    /// Cancelling the poll does not cancel server processing — the session commits anyway, but dirty
    /// records on the client would not be marked clean, causing a redundant re-push on the next sync.
    /// Future enhancement: a server-side cancel/abandon endpoint could mark the session as Cancelled
    /// before the queue processor picks it up (narrow window: Ready → Processing transition), but
    /// this requires careful handling of already-Processing sessions and orphaned temp table cleanup.
    /// For now, see it through to Committed, Failed, or Timeout.
    /// </remarks>
    private async Task WaitForPushCommitAsync(
        Guid sessionId,
        int completedTableIndex,
        int totalTables,
        IProgress<SyncProgress>? progress)
    {
        var timeout = TimeSpan.FromSeconds(_config.PushStatusTimeoutSeconds);
        var pollInterval = TimeSpan.FromMilliseconds(_config.PushStatusPollIntervalMs);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            var status = await _serverClient.GetPushStatusAsync(sessionId);

            if (status.Status == SyncConstants.STATUS_COMMITTED)
            {
                progress?.Report(new SyncProgress
                {
                    Phase = SyncPhase.PushComplete,
                    TablesCompleted = completedTableIndex,
                    TotalTables = totalTables,
                    StatusMessage = "Server committed push session."
                });
                return;
            }

            if (status.Status == SyncConstants.STATUS_FAILED)
                throw new InvalidOperationException($"Push session failed on server: {status.ErrorMessage}");

            progress?.Report(new SyncProgress
            {
                Phase = SyncPhase.PushWaiting,
                TablesCompleted = completedTableIndex,
                TotalTables = totalTables,
                StatusMessage = $"Waiting for server to commit... ({stopwatch.ElapsedMilliseconds:N0}ms)"
            });

            await Task.Delay(pollInterval);
        }

        throw new TimeoutException(
            $"Push session {sessionId} did not reach Committed status within {_config.PushStatusTimeoutSeconds}s. " +
            $"The session may still be processing on the server — dirty records will be re-pushed on the next sync cycle.");
    }

    /// <summary>
    /// Returns the number of locally modified records for the given table.
    /// </summary>
    /// <param name="table">The table configuration, including the strongly-typed handler.</param>
    /// <returns>Count of dirty records pending push.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the handler has not been initialized.</exception>
    private async Task<int> GetDirtyCountAsync(TableConfig table)
    {
        if (table.Handler == null)
            throw new InvalidOperationException($"Handler not initialized for table {table.TableName}. Use ClientSyncEngineBuilder.Build() to create the engine.");

        return await table.Handler.GetDirtyCountAsync();
    }

    /// <summary>
    /// Pushes all dirty records for a single table to the server in batches and reports progress.
    /// </summary>
    /// <param name="table">The table configuration, including the strongly-typed handler.</param>
    /// <param name="sessionId">The active push session ID.</param>
    /// <param name="tableIndex">Zero-based index of this table in the overall sync sequence, used for progress reporting.</param>
    /// <param name="totalTables">Total number of tables in the sync sequence (push + pull), used for progress reporting.</param>
    /// <param name="dirtyCount">Pre-fetched dirty record count, used for progress reporting.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total number of records pushed.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the handler has not been initialized.</exception>
    private async Task<int> PushTableAsync(
        TableConfig table,
        Guid sessionId,
        int tableIndex,
        int totalTables,
        int dirtyCount,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (table.Handler == null)
            throw new InvalidOperationException($"Handler not initialized for table {table.TableName}. Use ClientSyncEngineBuilder.Build() to create the engine.");

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PushTable,
            CurrentTable = table.TableName,
            TablesCompleted = tableIndex,
            TotalTables = totalTables,
            RecordsProcessed = 0,
            TotalRecords = dirtyCount,
            StatusMessage = $"Starting push for {table.TableName} ({dirtyCount:N0} records)..."
        });

        IProgress<(int Current, int Total)>? handlerProgress = null;
        if (progress != null)
        {
            handlerProgress = new Progress<(int Current, int Total)>(p =>
            {
                progress.Report(new SyncProgress
                {
                    Phase = SyncPhase.PushTable,
                    CurrentTable = table.TableName,
                    TablesCompleted = tableIndex,
                    TotalTables = totalTables,
                    RecordsProcessed = p.Current,
                    TotalRecords = p.Total,
                    StatusMessage = $"Pushing {table.TableName}: {p.Current:N0}/{p.Total:N0} records"
                });
            });
        }

        var pushed = await table.Handler.PushAsync(sessionId, handlerProgress, cancellationToken);

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PushTable,
            CurrentTable = table.TableName,
            TablesCompleted = tableIndex,
            TotalTables = totalTables,
            RecordsProcessed = pushed,
            TotalRecords = dirtyCount,
            StatusMessage = $"Completed {table.TableName}: {pushed:N0} records pushed."
        });

        return pushed;
    }

    /// <summary>
    /// Pulls all pending records for a single table from the server in batches and upserts them locally.
    /// </summary>
    /// <param name="table">The table configuration, including the strongly-typed handler.</param>
    /// <param name="pullSessionId">The active pull session ID.</param>
    /// <param name="tableIndex">Zero-based index of this table in the overall sync sequence, used for progress reporting.</param>
    /// <param name="totalTables">Total number of tables in the sync sequence (push + pull), used for progress reporting.</param>
    /// <param name="totalRecords">Expected record count from the pull begin response, used for progress reporting.</param>
    /// <param name="transaction">The ambient SQLite transaction for atomic local writes.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total records pulled and the list of sync session IDs processed.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the handler has not been initialized.</exception>
    private async Task<(int Pulled, List<Guid> SessionIds)> PullTableAsync(
        TableConfig table,
        Guid pullSessionId,
        int tableIndex,
        int totalTables,
        int totalRecords,
        IDbTransaction transaction,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (table.Handler == null)
            throw new InvalidOperationException($"Handler not initialized for table {table.TableName}. Use ClientSyncEngineBuilder.Build() to create the engine.");

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PullTable,
            CurrentTable = table.TableName,
            TablesCompleted = tableIndex,
            TotalTables = totalTables,
            RecordsProcessed = 0,
            TotalRecords = totalRecords,
            StatusMessage = $"Starting pull for {table.TableName} ({totalRecords:N0} records)..."
        });

        IProgress<(int Current, int Total)>? handlerProgress = null;
        if (progress != null)
        {
            handlerProgress = new Progress<(int Current, int Total)>(p =>
            {
                progress.Report(new SyncProgress
                {
                    Phase = SyncPhase.PullTable,
                    CurrentTable = table.TableName,
                    TablesCompleted = tableIndex,
                    TotalTables = totalTables,
                    RecordsProcessed = p.Current,
                    TotalRecords = p.Total,
                    StatusMessage = $"Pulling {table.TableName}: {p.Current:N0}/{p.Total:N0} records"
                });
            });
        }

        var (pulled, sessionIds) = await table.Handler.PullAsync(
            pullSessionId, transaction, handlerProgress, cancellationToken);

        progress?.Report(new SyncProgress
        {
            Phase = SyncPhase.PullTable,
            CurrentTable = table.TableName,
            TablesCompleted = tableIndex,
            TotalTables = totalTables,
            RecordsProcessed = pulled,
            TotalRecords = totalRecords,
            StatusMessage = $"Completed {table.TableName}: {pulled:N0} records pulled."
        });

        return (pulled, sessionIds);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
