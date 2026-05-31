using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Constants;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Server.Models;

namespace SyncSession.Server.Services;

/// <summary>
/// Detects and cleans up stale sessions that have timed out, and purges old
/// completed or failed sessions according to the configured retention policy.
/// </summary>
/// <remarks>
/// A session is considered stale when <c>LastActivityUtc</c> is older than
/// <see cref="ServerSyncConfiguration.SessionActivityTimeoutMinutes"/>.
/// Sessions older than <see cref="ServerSyncConfiguration.SessionRetentionDays"/> are purged.
/// Implements <see cref="ICleanupService"/> and delegates all data access to <see cref="IServerDatabase"/>.
/// </remarks>
public class SessionCleanupService : ICleanupService
{
    private readonly IServerDatabase _database;
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly TimeSpan _sessionTimeout;
    private readonly int _sessionRetentionDays;

    public SessionCleanupService(
        IServerDatabase database,
        ILogger<SessionCleanupService> logger,
        ServerSyncConfiguration config)
    {
        _database = database;
        _logger = logger;
        _sessionTimeout = TimeSpan.FromMinutes(config.SessionActivityTimeoutMinutes);
        _sessionRetentionDays = config.SessionRetentionDays;
    }

    /// <inheritdoc/>
    public async Task<int> ExecuteCleanupAsync()
    {
        var staleCount = await CleanupStaleSessions();
        var purgedCount = await PurgeOldSessions(_sessionRetentionDays);
        return staleCount + purgedCount;
    }

    /// <inheritdoc/>
    public string GetCleanupDescription() =>
        "Session cleanup (stale-session failover + optional retention purge)";

    /// <summary>
    /// Find all stale sessions (inactive beyond timeout threshold)
    /// </summary>
    public virtual async Task<List<SessionRecord>> FindStaleSessions()
    {
        var cutoffTime = DateTime.UtcNow.Subtract(_sessionTimeout);
        return await _database.FindStaleSessionsAsync(cutoffTime);
    }

    /// <summary>
    /// Mark stale sessions as Failed and cleanup their temp tables
    /// </summary>
    public virtual async Task<int> CleanupStaleSessions()
    {
        var staleSessions = await FindStaleSessions();

        if (!staleSessions.Any())
        {
            _logger.LogDebug("No stale sessions found");
            return 0;
        }

        _logger.LogWarning("Found {Count} stale sessions to cleanup", staleSessions.Count);

        int cleanedCount = 0;

        foreach (var session in staleSessions)
        {
            try
            {
                await CleanupStaleSession(session);
                cleanedCount++;

                _logger.LogInformation("Cleaned up stale session {SessionId} (inactive for {InactiveDuration})",
                    session.SessionId, DateTime.UtcNow - session.LastActivityUtc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup stale session {SessionId}", session.SessionId);
            }
        }

        return cleanedCount;
    }

    /// <summary>
    /// Cleanup a single stale session (handles both Push and Pull session types)
    /// </summary>
    private async Task CleanupStaleSession(SessionRecord session)
    {
        // Get temp tables for this session
        var tempTables = await _database.GetSessionTempTableInfoAsync(session.SessionId);

        if (session.SessionType == "Pull")
        {
            // Pull sessions: shared tables use SessionId column
            var sharedTableNames = tempTables
                .Where(t => t.UsesSharedTable)
                .Select(t => t.TempTableName)
                .ToArray();

            if (sharedTableNames.Length > 0)
            {
                int rowsDeleted = await _database.DeletePullSessionDataAsync(session.SessionId, sharedTableNames);
                _logger.LogDebug("Deleted {RowCount} rows from shared pull tables for session {SessionId}",
                    rowsDeleted, session.SessionId);
            }

            // Drop dedicated pull tables
            foreach (var table in tempTables.Where(t => !t.UsesSharedTable))
            {
                await _database.DropTempTableAsync(table.TempTableName);
                _logger.LogDebug("Dropped dedicated pull table {TempTableName}", table.TempTableName);
            }
        }
        else
        {
            // Push sessions: shared tables use SessionId column
            foreach (var table in tempTables)
            {
                if (table.UsesSharedTable)
                {
                    int rowsDeleted = await _database.DeleteFromSharedTempTableAsync(table.TempTableName, session.SessionId);
                    _logger.LogDebug("Deleted {RowCount} rows from shared table {TempTableName}",
                        rowsDeleted, table.TempTableName);
                }
                else
                {
                    await _database.DropTempTableAsync(table.TempTableName);
                    _logger.LogDebug("Dropped dedicated table {TempTableName}", table.TempTableName);
                }
            }
        }

        // Mark session as Failed
        var errorMessage = $"Session timed out after {_sessionTimeout.TotalMinutes} minutes of inactivity";
        await _database.MarkSessionFailedAsync(session.SessionId, errorMessage);

        // Delete session table records
        await _database.DeleteSessionTablesAsync(session.SessionId);
    }

    /// <summary>
    /// Delete old completed/failed sessions (older than retentionDays)
    /// </summary>
    public virtual async Task<int> PurgeOldSessions(int retentionDays = 30)
    {
        if (retentionDays <= 0)
        {
            _logger.LogDebug(
                "Session purge disabled (SessionRetentionDays={RetentionDays}) — retaining all sessions to prevent the offline-client gap",
                retentionDays);
            return 0;
        }

        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
        var statuses = new[] { SyncConstants.STATUS_COMMITTED, SyncConstants.STATUS_COMPLETED, SyncConstants.STATUS_FAILED };

        var sessionIds = await _database.FindOldSessionsAsync(cutoffDate, statuses);

        if (!sessionIds.Any())
        {
            _logger.LogDebug("No old sessions to purge (retention: {RetentionDays} days)", retentionDays);
            return 0;
        }

        _logger.LogInformation("Purging {Count} old sessions (older than {RetentionDays} days)",
            sessionIds.Count, retentionDays);

        // Delete ClientProcessedSessions
        await _database.DeleteClientProcessedSessionsAsync(sessionIds);

        // Delete SyncSessions and SyncSessionTables
        await _database.DeleteSessionsAsync(sessionIds);

        _logger.LogInformation("Purged {Count} old sessions", sessionIds.Count);

        return sessionIds.Count;
    }
}
