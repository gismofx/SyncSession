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

            await _database.ExecuteInTransactionAsync(async transaction =>
            {
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
