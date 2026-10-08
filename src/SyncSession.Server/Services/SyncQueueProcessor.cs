using System.Data;
using System.Text.Json;
using SyncSession.Core.Constants;
using SyncSession.Core.Exceptions;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Server.Services;

/// <summary>
/// Processes sync sessions from the background queue.
/// </summary>
/// <remarks>
/// Upserts records from temp tables into main tables within an atomic transaction.
/// Session versions are auto-assigned by the database on session creation.
/// </remarks>
internal class SyncQueueProcessor : ISyncQueueProcessor
{
    private readonly IServerDatabase _database;
    private readonly ITempTableManager _tempTableManager;
    private readonly ILogger<SyncQueueProcessor> _logger;

    public SyncQueueProcessor(
        IServerDatabase database,
        ITempTableManager tempTableManager,
        ILogger<SyncQueueProcessor> logger)
    {
        _database = database;
        _tempTableManager = tempTableManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> ProcessReadySessionsAsync(CancellationToken cancellationToken)
    {
        var sessions = await _database.FindReadySessionsAsync();
        
        foreach (var session in sessions)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await ProcessSessionAsync(session.SessionId);
        }

        return sessions.Count;
    }

    /// <summary>
    /// Processes a single sync session atomically.
    /// </summary>
    /// <param name="sessionId">The session ID to process.</param>
    /// <remarks>
    /// Marks the session as Processing, upserts all tables within a transaction, then marks as Committed.
    /// On failure, marks the session as Failed and re-throws. Exposed as <c>internal</c> for unit testing.
    /// </remarks>
    internal async Task ProcessSessionAsync(Guid sessionId)
    {
        _logger.LogInformation("Processing session {SessionId}", sessionId);

        try
        {
            await _database.UpdateSessionStatusAsync(sessionId, SyncConstants.STATUS_PROCESSING);

            var session = await _database.GetSessionAsync(sessionId);
            if (session == null)
                throw new SyncException($"Session {sessionId} not found");

            if (!session.SyncVersion.HasValue)
                throw new SyncException($"Session {sessionId} has no version - database AUTO_INCREMENT failed");

            _logger.LogDebug("Processing session {SessionId} with version {Version}",
                sessionId, session.SyncVersion.Value);

            var tables = await _database.GetSessionTableDetailsAsync(sessionId);
            var rowCountsByTable = new Dictionary<string, int>();

            await ExecuteRetryingDeadlocksAsync(sessionId, async transaction =>
            {
                rowCountsByTable.Clear(); // a retried attempt starts from nothing
                foreach (var table in tables.OrderBy(t => t.Priority))
                {
                    var rows = await ProcessTableAsync(sessionId, table, transaction);
                    rowCountsByTable[table.TableName] = rows;
                }

                // 38l: Status + row counts written atomically in same transaction
                var totalRows = rowCountsByTable.Values.Sum();
                var rowCountsJson = JsonSerializer.Serialize(rowCountsByTable);
                await _database.UpdateSessionStatusAsync(
                    sessionId, SyncConstants.STATUS_COMMITTED, transaction,
                    totalRows: totalRows, rowCountsJson: rowCountsJson);

                // The pushing device already holds these records; without this it would see its own
                // committed session as "unseen" on the next pull, re-download its own rows and
                // overwrite its local copies. Marked inside the commit transaction so a rollback
                // can never leave the device recorded as having seen records it never received.
                if (session.DeviceId.HasValue)
                {
                    await _database.MarkSessionsProcessedAsync(
                        session.DeviceId.Value, new[] { sessionId }, transaction);
                }
            });

            await _tempTableManager.CleanupSessionTablesAsync(sessionId);
            await _database.DeleteSessionTablesAsync(sessionId);

            _logger.LogInformation("Successfully processed session {SessionId} with version {Version}",
                sessionId, session.SyncVersion.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process session {SessionId}", sessionId);
            await _database.UpdateSessionStatusAsync(sessionId, SyncConstants.STATUS_FAILED, errorMessage: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// How many times a commit transaction is attempted when it keeps losing deadlocks.
    /// </summary>
    internal const int MaxCommitAttempts = 3;

    /// <summary>
    /// Runs the commit transaction, retrying it when the database picks it as a deadlock victim.
    /// </summary>
    /// <remarks>
    /// MySQL/MariaDB roll the whole transaction back on a deadlock (error 1213, SQLSTATE 40001) and
    /// ask the client to restart it; nothing has been committed and the staged temp-table rows are
    /// untouched, so running the same work again is safe. Any other error — including a lock-wait
    /// timeout, which is not a rollback of the whole transaction — fails the session as before.
    /// </remarks>
    private async Task ExecuteRetryingDeadlocksAsync(Guid sessionId, Func<IDbTransaction, Task> operations)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _database.ExecuteInTransactionAsync(operations);
                return;
            }
            catch (Exception ex) when (attempt < MaxCommitAttempts && IsDeadlock(ex))
            {
                _logger.LogWarning(ex,
                    "Commit of session {SessionId} lost a deadlock (attempt {Attempt} of {MaxAttempts}); retrying",
                    sessionId, attempt, MaxCommitAttempts);
                await Task.Delay(Random.Shared.Next(50, 150) * attempt);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/>, or any exception inside it, is a deadlock-victim rollback:
    /// SQLSTATE 40001, which is what MySQL and MariaDB report for error 1213.
    /// </summary>
    internal static bool IsDeadlock(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Data.Common.DbException { SqlState: "40001" })
                return true;
        }
        return false;
    }

    private async Task<int> ProcessTableAsync(
        Guid sessionId,
        SessionTableInfo table,
        IDbTransaction transaction)
    {
        _logger.LogDebug("Processing table {TableName} for session {SessionId}",
            table.TableName, sessionId);

        var rowsAffected = await _database.UpsertFromTempTableAsync(
            table.TableName,
            table.TempTableName,
            table.UsesSharedTable,
            sessionId,
            transaction);

        _logger.LogDebug("Upserted {RowCount} rows into {TableName}", rowsAffected, table.TableName);
        return rowsAffected;
    }
}
