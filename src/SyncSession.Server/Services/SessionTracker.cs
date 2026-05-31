using System.Linq;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Server.Services;

/// <summary>
/// Manages sync session lifecycle including push and pull session creation, status tracking, and completion.
/// </summary>
internal class SessionTracker : ISessionTracker
{
    private readonly IServerDatabase _database;
    private readonly ITempTableManager _tempTableManager;
    private readonly ILogger<SessionTracker> _logger;

    public SessionTracker(
        IServerDatabase database,
        ITempTableManager tempTableManager,
        ILogger<SessionTracker> logger)
    {
        _database = database;
        _tempTableManager = tempTableManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<PushSessionBeginResponse> CreatePushSessionAsync(
        PushSessionBeginRequest request, string? userId = null, string? userDisplayName = null)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (request.DeviceId == Guid.Empty)
            throw new ArgumentException("DeviceId cannot be empty", nameof(request));
        if (request.Tables == null || !request.Tables.Any())
            throw new ArgumentException("At least one table must be specified", nameof(request));
        if (request.Tables.Any(t => string.IsNullOrWhiteSpace(t.TableName)))
            throw new ArgumentException("Table names cannot be null or empty", nameof(request));

        var sessionId = Guid.NewGuid();
        var session = new SessionRecord
        {
            SessionId = sessionId,
            TenantId = request.TenantId,
            DeviceId = request.DeviceId,
            UserId = userId,
            UserDisplayName = userDisplayName,
            SessionType = SyncConstants.SESSION_TYPE_PUSH,
            Status = SyncConstants.STATUS_STAGING,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow
        };

        await _database.CreateSessionAsync(session);

        var response = new PushSessionBeginResponse
        {
            Success = true,
            SessionId = sessionId
        };

        // Determine temp table strategy for each table and track in SyncSessionTables
        var priority = 1;
        foreach (var table in request.Tables)
        {
            var tableInfo = await _tempTableManager.GetTempTableForPushAsync(
                sessionId,
                table.TableName,
                table.EstimatedRecordCount);

            response.Tables[table.TableName] = new SyncSessionTableMetadata
            {
                TableName = table.TableName,
                TempTableName = tableInfo.TempTableName,
                UsesSharedTable = tableInfo.UsesSharedTable,
                TotalRecords = null  // Not applicable for push
            };

        // Insert into SyncSessionTables via database abstraction
            await _database.InsertSessionTableAsync(sessionId, table.TableName, tableInfo.TempTableName, priority, tableInfo.UsesSharedTable, table.EstimatedRecordCount);
            priority++;
        }

        _logger.LogInformation("Created push session {SessionId} for device {DeviceId} with {TableCount} tables",
            sessionId, request.DeviceId, request.Tables.Count());

        return response;
    }

    /// <inheritdoc/>
    public async Task<PullSessionBeginResponse> CreatePullSessionAsync(
        PullSessionBeginRequest request, string? userId = null, string? userDisplayName = null)
    {
        // Input validation
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (request.DeviceId == Guid.Empty)
            throw new ArgumentException("DeviceId cannot be empty", nameof(request));
        if (request.TableNames == null || !request.TableNames.Any())
            throw new ArgumentException("At least one table must be specified", nameof(request));
        if (request.TableNames.Any(t => string.IsNullOrWhiteSpace(t)))
            throw new ArgumentException("Table names cannot be null or empty", nameof(request));

        var pullSessionId = Guid.NewGuid();

        // Find unseen sessions for this device
        var unseenSessionIds = (await _database.FindUnseenSessionIdsAsync(request.DeviceId)).ToList();

        // If no unseen sessions, return empty response (no temp data to protect, no session record needed)
        if (!unseenSessionIds.Any())
        {
            _logger.LogInformation("No unseen sessions for device {DeviceId} - nothing to pull",
                request.DeviceId);
            
            return new PullSessionBeginResponse
            {
                Success = true,
                PullSessionId = pullSessionId,
                Tables = new Dictionary<string, SyncSessionTableMetadata>() // Empty
            };
        }

        // Create pull session record for activity tracking and stale session cleanup
        var pullSession = new SessionRecord
        {
            SessionId = pullSessionId,
            TenantId = request.TenantId,
            DeviceId = request.DeviceId,
            UserId = userId,
            UserDisplayName = userDisplayName,
            SessionType = SyncConstants.SESSION_TYPE_PULL,
            Status = SyncConstants.STATUS_PULLING,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow
        };
        await _database.CreateSessionAsync(pullSession);

        // Snapshot records from unseen sessions into temp pull tables
        // Use hybrid strategy: count records first, then choose shared vs dedicated
        var tablePullInfo = new Dictionary<string, SyncSessionTableMetadata>();
        var priority = 1;
        
        foreach (var tableName in request.TableNames)
        {
            // 1. Count records that would be pulled
            var unseenRecordCount = await _database.CountRecordsFromSessionsAsync(tableName, unseenSessionIds, request.TenantId);
            
            // 2. Determine temp table strategy (shared vs dedicated)
            var tableInfo = await _tempTableManager.GetTempTableForPullAsync(
                pullSessionId,
                tableName,
                unseenRecordCount);
            
            // 3. Snapshot records into chosen temp table
            var recordCount = await _database.SnapshotRecordsForPullAsync(
                tableInfo.TempTableName,
                tableName,
                unseenSessionIds,
                pullSessionId,
                tableInfo.UsesSharedTable,
                request.TenantId);
            
            // 4. Track in SyncSessionTables for stale session cleanup
            await _database.InsertSessionTableAsync(pullSessionId, tableName, tableInfo.TempTableName, priority, tableInfo.UsesSharedTable, recordCount);
            priority++;
            
            // 5. Store metadata for response
            tablePullInfo[tableName] = new SyncSessionTableMetadata
            {
                TableName = tableName,
                TempTableName = tableInfo.TempTableName,
                UsesSharedTable = tableInfo.UsesSharedTable,
                TotalRecords = recordCount
            };
            
            _logger.LogDebug(
                "Pull session {PullSessionId} table {TableName}: {RecordCount} records, {Strategy} table ({TempTable})",
                pullSessionId, tableName, recordCount, tableInfo.UsesSharedTable ? "shared" : "dedicated", tableInfo.TempTableName);
        }

        _logger.LogInformation(
            "Created pull session {PullSessionId} for device {DeviceId}: {UnseenCount} unseen sessions, {TableCount} tables",
            pullSessionId, request.DeviceId, unseenSessionIds.Count, request.TableNames.Count());

        return new PullSessionBeginResponse
        {
            Success = true,
            PullSessionId = pullSessionId,
            Tables = tablePullInfo
        };
    }

    /// <inheritdoc/>
    public async Task<SessionRecord?> GetSessionAsync(Guid sessionId)
    {
        return await _database.GetSessionAsync(sessionId);
    }

    /// <inheritdoc/>
    public async Task UpdateSessionActivityAsync(Guid sessionId)
    {
        await _database.UpdateSessionActivityAsync(sessionId);
        _logger.LogDebug("Updated activity for session {SessionId}", sessionId);
    }

    /// <inheritdoc/>
    public async Task<bool> MarkSessionReadyAsync(Guid sessionId)
    {
        var success = await _database.MarkSessionReadyAsync(sessionId);

        if (success)
        {
            _logger.LogInformation("Marked session {SessionId} as Ready", sessionId);
        }

        return success;
    }

    /// <inheritdoc/>
    public async Task<bool> SessionExistsAsync(Guid sessionId, string? expectedStatus = null)
    {
        return await _database.SessionExistsAsync(sessionId, expectedStatus);
    }

    /// <inheritdoc/>
    public async Task<PushTableCompleteResponse> CompleteTableAsync(
        Guid sessionId,
        string tableName,
        int totalRecordsSent)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));

        // Get temp table info
        var tableInfo = await _database.GetSessionTableInfoAsync(sessionId, tableName);

        if (tableInfo == null)
        {
            return new PushTableCompleteResponse
            {
                Success = false,
                ErrorMessage = $"Table {tableName} not found for session {sessionId}"
            };
        }

        // Count records in temp table
        var actualCount = await _database.CountTempTableRecordsAsync(
            tableInfo.Value.TempTableName, 
            sessionId, 
            tableInfo.Value.UsesSharedTable);

        // Update SyncSessionTables status
        await _database.UpdateSessionTableStatusAsync(sessionId, tableName, actualCount, "Ready");

        var matches = actualCount == totalRecordsSent;

        _logger.LogInformation(
            "Completed table {TableName} for session {SessionId}: sent={Sent}, actual={Actual}, matches={Matches}",
            tableName, sessionId, totalRecordsSent, actualCount, matches);

        return new PushTableCompleteResponse
        {
            Success = true,
            ActualRecordCount = actualCount,
            CountMatches = matches,
            ErrorMessage = matches ? null : $"Count mismatch: expected {totalRecordsSent}, got {actualCount}"
        };
    }

    /// <inheritdoc/>
    public async Task<PushSessionStatusResponse?> GetSessionStatusAsync(Guid sessionId)
    {
        var session = await _database.GetSessionAsync(sessionId);

        if (session == null)
            return null;

        return new PushSessionStatusResponse
        {
            SessionId = session.SessionId,
            Status = session.Status,
            SyncVersion = session.SyncVersion,
            ErrorMessage = session.ErrorMessage,
            CreatedAtUtc = session.CreatedAtUtc,
            LastActivityUtc = session.LastActivityUtc,
            CommittedAtUtc = session.CommittedAtUtc
        };
    }


    /// <inheritdoc/>
    public async Task UpdatePullSessionActivityAsync(Guid pullSessionId)
    {
        await _database.UpdateSessionActivityAsync(pullSessionId);
        _logger.LogDebug("Updated activity for pull session {PullSessionId}", pullSessionId);
    }
}
