using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using SyncSession.Core.Attributes;
using SyncSession.Core.DTOs;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;
using SyncSession.Server.Models;

namespace SyncSession.Server.Database;

/// <summary>
/// MySQL implementation of <see cref="IServerDatabase"/> for production server deployments.
/// </summary>
public class MySqlServerDatabase : IServerDatabase
{
    private const string TempTableSessionIdColumn = "SessionId";

    private readonly string _connectionString;
    private readonly ITableMetadataCache _metadataCache;
    private readonly ServerSyncConfiguration _config;
    private readonly ILogger<MySqlServerDatabase>? _logger;

    public MySqlServerDatabase(
        string connectionString,
        ITableMetadataCache metadataCache,
        ServerSyncConfiguration config,
        ILogger<MySqlServerDatabase>? logger = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _metadataCache = metadataCache ?? throw new ArgumentNullException(nameof(metadataCache));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IDbConnection> GetConnectionAsync()
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <inheritdoc/>
    public async Task ExecuteInTransactionAsync(Func<IDbTransaction, Task> operations)
    {
        using var connection = await GetConnectionAsync();
        using var transaction = connection.BeginTransaction(_config.TransactionIsolationLevel);

        try
        {
            await operations(transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task CreateDedicatedTempTableAsync(string tempTableName, string sourceTableName)
    {
        // Simply copy table structure - no AUTO_INCREMENT conflicts since business tables don't have it
        var sql = $"CREATE TABLE IF NOT EXISTS `{tempTableName}` LIKE `{sourceTableName}`";

        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(sql);

        _logger?.LogInformation("Created dedicated temp table {TempTableName}", tempTableName);
    }

    /// <inheritdoc/>
    public async Task DropTempTableAsync(string tempTableName)
    {
        var sql = $"DROP TABLE IF EXISTS `{tempTableName}`";

        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(sql);

        _logger?.LogDebug("Dropped temp table {TempTableName}", tempTableName);
    }

    /// <inheritdoc/>
    public async Task<bool> TempTableExistsAsync(string tempTableName)
    {
        var sql = @"
            SELECT COUNT(*)
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @TableName";

        using var connection = await GetConnectionAsync();
        var count = await connection.ExecuteScalarAsync<int>(sql, new { TableName = tempTableName });
        return count > 0;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<T>> GetRecordsFromSessionsAsync<T>(IEnumerable<Guid> sessionIds, Guid? tenantId = null) where T : ISyncEntity
    {
        var sessionIdList = sessionIds.ToList();
        if (!sessionIdList.Any())
            return Enumerable.Empty<T>();

        var tableName = TableNameResolver.GetTableName<T>();
        var sessionIdStrings = sessionIdList.Select(id => id.ToString()).ToList();
        var columns = EntityReflectionHelper.GetColumnsForServerSelect<T>();
        var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));

        var sql = $@"
            SELECT {columnList}, ModifiedAtUtc, SyncSessionId
            FROM `{tableName}`
            WHERE SyncSessionId IN @SessionIds";

        // Add tenant filtering
        if (typeof(IMultiTenantSyncEntity).IsAssignableFrom(typeof(T)) && tenantId.HasValue)
        {
            sql += " AND TenantId = @TenantId";
        }

        using var connection = await GetConnectionAsync();
        
        var records = typeof(IMultiTenantSyncEntity).IsAssignableFrom(typeof(T)) && tenantId.HasValue
            ? await connection.QueryAsync<T>(sql, new { SessionIds = sessionIdStrings, TenantId = tenantId })
            : await connection.QueryAsync<T>(sql, new { SessionIds = sessionIdStrings });

        return records;
    }

    /// <inheritdoc/>
    public async Task CreateSessionAsync(SessionRecord session, IDbTransaction? transaction = null)
    {
        var sql = @"
            INSERT INTO SessionRecords 
            (SessionId, TenantId, DeviceId, UserId, UserDisplayName,
             SessionType, Status, CreatedAtUtc, LastActivityUtc,
             CommittedAtUtc, ErrorMessage, TotalRows, RowCountsJson)
            VALUES (@SessionId, @TenantId, @DeviceId, @UserId, @UserDisplayName,
                    @SessionType, @Status, @CreatedAtUtc, @LastActivityUtc,
                    @CommittedAtUtc, @ErrorMessage, @TotalRows, @RowCountsJson)";

        var parameters = new
        {
            SessionId = session.SessionId.ToString(),
            TenantId = session.TenantId?.ToString(),
            DeviceId = session.DeviceId?.ToString(),
            session.UserId,
            session.UserDisplayName,
            session.SessionType,
            session.Status,
            session.CreatedAtUtc,
            session.LastActivityUtc,
            session.CommittedAtUtc,
            session.ErrorMessage,
            session.TotalRows,
            session.RowCountsJson,
        };

        if (transaction != null)
        {
            await transaction.Connection!.ExecuteAsync(sql, parameters, transaction);
        }
        else
        {
            using var connection = await GetConnectionAsync();
            await connection.ExecuteAsync(sql, parameters);
        }

        _logger?.LogInformation("Created session {SessionId} for device {DeviceId}",
            session.SessionId, session.DeviceId);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<Guid>> FindUnseenSessionIdsAsync(Guid deviceId)
    {
        var sql = @"
            SELECT CAST(s.SessionId AS CHAR(36)) AS SessionId
            FROM SessionRecords s
            WHERE s.Status = 'Committed'
              AND NOT EXISTS (
                  SELECT 1
                  FROM ClientProcessedSessions cps
                  WHERE cps.DeviceId = @DeviceId
                    AND cps.SessionId = s.SessionId
              )
            ORDER BY s.SyncVersion";

        using var connection = await GetConnectionAsync();
        var sessionIds = await connection.QueryAsync<string>(sql, new { DeviceId = deviceId.ToString() });
        return sessionIds.Select(id => Guid.Parse(id));
    }

    /// <inheritdoc/>
    public async Task AcknowledgeSeedAsync(Guid deviceId, Guid tenantId)
    {
        var sql = @"
            INSERT IGNORE INTO ClientProcessedSessions (DeviceId, SessionId, ProcessedAtUtc)
            SELECT @DeviceId, SessionId, UTC_TIMESTAMP(6)
            FROM SessionRecords
            WHERE Status = 'Committed'
              AND (TenantId = @TenantId OR TenantId IS NULL)";

        using var connection = await GetConnectionAsync();
        var rowsInserted = await connection.ExecuteAsync(sql, new { DeviceId = deviceId.ToString(), TenantId = tenantId.ToString() });

        _logger?.LogInformation("Seed acknowledge for device {DeviceId} tenant {TenantId}: marked {Count} sessions as processed",
            deviceId, tenantId, rowsInserted);
    }

    /// <inheritdoc/>
    public async Task MarkSessionsProcessedAsync(Guid deviceId, IEnumerable<Guid> sessionIds, IDbTransaction? transaction = null)
    {
        var sql = @"
            INSERT IGNORE INTO ClientProcessedSessions (DeviceId, SessionId, ProcessedAtUtc)
            VALUES (@DeviceId, @SessionId, UTC_TIMESTAMP(6))";

        var parameters = sessionIds.Select(sessionId => new
        {
            DeviceId = deviceId.ToString(),
            SessionId = sessionId.ToString()
        }).ToList();

        if (transaction != null)
        {
            // Transactional path - required when marking the pushing device at session commit,
            // so a rolled-back session never leaves the device recorded as having seen it.
            await transaction.Connection!.ExecuteAsync(sql, parameters, transaction);
        }
        else
        {
            using var connection = await GetConnectionAsync();
            await connection.ExecuteAsync(sql, parameters);
        }

        _logger?.LogDebug("Marked {SessionCount} sessions as processed for device {DeviceId}", 
            sessionIds.Count(), deviceId);
    }

    
    #region Cleanup Operations
    
    /// <inheritdoc/>
    public async Task<List<SessionRecord>> FindStaleSessionsAsync(DateTime cutoffTime)
    {
        using var connection = await GetConnectionAsync();
        
        var sessions = await connection.QueryAsync<dynamic>(@"
            SELECT SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc
            FROM SessionRecords
            WHERE Status IN ('Staging', 'Ready', 'Processing', 'Pulling')
              AND LastActivityUtc < @CutoffTime
            ORDER BY LastActivityUtc",
            new { CutoffTime = cutoffTime });
        
        return sessions.Select(s => new SessionRecord
        {
            SessionId = (Guid)s.SessionId,
            SessionType = s.SessionType,
            Status = s.Status,
            CreatedAtUtc = (DateTime)s.CreatedAtUtc,
            LastActivityUtc = (DateTime)s.LastActivityUtc
        }).ToList();
    }
    
    /// <inheritdoc/>
    public async Task MarkSessionFailedAsync(Guid sessionId, string errorMessage)
    {
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync(@"
            UPDATE SessionRecords
            SET Status = 'Failed',
                ErrorMessage = @ErrorMessage
            WHERE SessionId = @SessionId",
            new
            {
                SessionId = sessionId.ToString(),
                ErrorMessage = errorMessage
            });
    }
    
    /// <inheritdoc/>
    public async Task<List<TempTableInfo>> GetSessionTempTableInfoAsync(Guid sessionId)
    {
        using var connection = await GetConnectionAsync();
        
        var tables = await connection.QueryAsync<dynamic>(@"
            SELECT TempTableName, UsesSharedTable
            FROM SyncSessionTables
            WHERE SessionId = @SessionId
              AND TempTableName IS NOT NULL",
            new { SessionId = sessionId.ToString() });
        
        return tables.Select(t => new TempTableInfo
        {
            TempTableName = (string)t.TempTableName,
            UsesSharedTable = (bool)t.UsesSharedTable
        }).ToList();
    }
    
    /// <inheritdoc/>
    public async Task<int> DeleteFromSharedTempTableAsync(string tableName, Guid sessionId)
    {
        using var connection = await GetConnectionAsync();
        
        return await connection.ExecuteAsync(
            $"DELETE FROM {tableName} WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
    }
    
    /// <inheritdoc/>
    public async Task DeleteSessionTablesAsync(Guid sessionId)
    {
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync(@"
            DELETE FROM SyncSessionTables
            WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
    }
    
    /// <inheritdoc/>
    public async Task<List<Guid>> FindOldSessionsAsync(DateTime cutoffDate, string[] statuses)
    {
        using var connection = await GetConnectionAsync();
        
        var sessions = await connection.QueryAsync<string>(@"
            SELECT SessionId
            FROM SessionRecords
            WHERE Status IN @Statuses
              AND CreatedAtUtc < @CutoffDate",
            new
            {
                Statuses = statuses,
                CutoffDate = cutoffDate
            });
        
        return sessions.Select(s => Guid.Parse(s)).ToList();
    }
    
    /// <inheritdoc/>
    public async Task DeleteClientProcessedSessionsAsync(IEnumerable<Guid> sessionIds)
    {
        using var connection = await GetConnectionAsync();
        
        var sessionIdStrings = sessionIds.Select(s => s.ToString()).ToList();
        await connection.ExecuteAsync(@"
            DELETE FROM ClientProcessedSessions
            WHERE SessionId IN @SessionIds",
            new { SessionIds = sessionIdStrings });
    }
    
    /// <inheritdoc/>
    public async Task DeleteSessionsAsync(IEnumerable<Guid> sessionIds)
    {
        using var connection = await GetConnectionAsync();
        
        var sessionIdStrings = sessionIds.Select(s => s.ToString()).ToList();
        
        // First delete session tables
        await connection.ExecuteAsync(@"
            DELETE FROM SyncSessionTables
            WHERE SessionId IN @SessionIds",
            new { SessionIds = sessionIdStrings });
        
        // Then delete sessions
        await connection.ExecuteAsync(@"
            DELETE FROM SessionRecords
            WHERE SessionId IN @SessionIds",
            new { SessionIds = sessionIdStrings });
    }
    
    /// <inheritdoc/>
    public async Task<int> DeleteOldSharedTempRowsAsync(string tableName, DateTime cutoffTime)
    {
        using var connection = await GetConnectionAsync();
        
        // Check if table exists first
        var tableExists = await connection.ExecuteScalarAsync<int>($@"
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
              AND table_name = '{tableName}'") > 0;
        
        if (!tableExists)
            return 0;
        
        return await connection.ExecuteAsync(
            $@"DELETE FROM {tableName} WHERE {TempTableSessionIdColumn} IN (
               SELECT SessionId FROM SessionRecords WHERE CreatedAtUtc < @CutoffTime)",
            new { CutoffTime = cutoffTime });
    }
    
    /// <inheritdoc/>
    public async Task<List<string>> FindDedicatedTempTablesAsync()
    {
        using var connection = await GetConnectionAsync();
        
        var tables = await connection.QueryAsync<string>(@"
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
              AND (
                  (table_name LIKE 'TempPush\_%' ESCAPE '\\')
                  OR (table_name LIKE 'TempPull\_%' ESCAPE '\\')
              )
            ORDER BY table_name");
        
        return tables.ToList();
    }
    
    /// <inheritdoc/>
    public async Task<List<string>> FindActiveTempTableNamesAsync()
    {
        using var connection = await GetConnectionAsync();
        
        var activeTables = await connection.QueryAsync<string>(@"
            SELECT DISTINCT TempTableName
            FROM SyncSessionTables
            WHERE TempTableName IS NOT NULL
              AND UsesSharedTable = 0
              AND SessionId IN (
                  SELECT SessionId
                  FROM SessionRecords
                  WHERE Status IN ('Staging', 'Ready', 'Processing', 'Pulling')
              )");
        
        return activeTables.ToList();
    }
    
    /// <inheritdoc/>
    public async Task<int> CountSharedTempTableRowsAsync(string tableName)
    {
        using var connection = await GetConnectionAsync();
        
        // Check if table exists first
        var tableExists = await connection.ExecuteScalarAsync<int>($@"
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
              AND table_name = '{tableName}'") > 0;
        
        if (!tableExists)
            return 0;
        
        return await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM {tableName}");
    }
    
    #endregion
    
    #region Session Lifecycle Operations
    
    /// <inheritdoc/>
    public async Task InsertSessionTableAsync(
        Guid sessionId,
        string tableName,
        string tempTableName,
        int priority,
        bool usesSharedTable,
        int estimatedRecordCount)
    {
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync(@"
            INSERT INTO SyncSessionTables 
            (SessionId, TableName, TempTableName, ProcessingPriority, UsesSharedTable, EstimatedRecordCount, Status)
            VALUES (@SessionId, @TableName, @TempTableName, @Priority, @UsesSharedTable, @EstimatedRecordCount, 'Staging')",
            new
            {
                SessionId = sessionId.ToString(),
                TableName = tableName,
                TempTableName = tempTableName,
                Priority = priority,
                UsesSharedTable = usesSharedTable,
                EstimatedRecordCount = estimatedRecordCount
            });
    }
    
    /// <inheritdoc/>
    public async Task<SessionRecord?> GetSessionAsync(Guid sessionId)
    {
        using var connection = await GetConnectionAsync();
        
        var sessions = await connection.QueryAsync<SessionRecord>(@"
            SELECT SessionId, TenantId, DeviceId, UserId, UserDisplayName,
                   SessionType, Status, SyncVersion,
                   CreatedAtUtc, LastActivityUtc, CommittedAtUtc, ErrorMessage,
                   TotalRows, RowCountsJson
            FROM SessionRecords
            WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
        
        return sessions.FirstOrDefault();
    }
    
    /// <inheritdoc/>
    public async Task UpdateSessionActivityAsync(Guid sessionId)
    {
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync(@"
            UPDATE SessionRecords
            SET LastActivityUtc = UTC_TIMESTAMP(6)
            WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
    }
    
    /// <inheritdoc/>
    public async Task<bool> MarkSessionReadyAsync(Guid sessionId)
    {
        using var connection = await GetConnectionAsync();
        
        var rowsAffected = await connection.ExecuteAsync(@"
            UPDATE SessionRecords
            SET Status = 'Ready',
                LastActivityUtc = UTC_TIMESTAMP(6)
            WHERE SessionId = @SessionId
              AND Status = 'Staging'",
            new { SessionId = sessionId.ToString() });
        
        return rowsAffected > 0;
    }
    
    /// <inheritdoc/>
    public async Task<bool> SessionExistsAsync(Guid sessionId, string? expectedStatus = null)
    {
        using var connection = await GetConnectionAsync();
        
        var sql = expectedStatus == null
            ? "SELECT COUNT(*) FROM SessionRecords WHERE SessionId = @SessionId"
            : "SELECT COUNT(*) FROM SessionRecords WHERE SessionId = @SessionId AND Status = @Status";
        
        var count = await connection.ExecuteScalarAsync<int>(sql, new
        {
            SessionId = sessionId.ToString(),
            Status = expectedStatus
        });
        
        return count > 0;
    }
    
    /// <inheritdoc/>
    public async Task<TempTableInfo?> GetSessionTableInfoAsync(
        Guid sessionId,
        string tableName)
    {
        using var connection = await GetConnectionAsync();
        
        var result = await connection.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT TempTableName, UsesSharedTable
            FROM SyncSessionTables
            WHERE SessionId = @SessionId AND TableName = @TableName",
            new { SessionId = sessionId.ToString(), TableName = tableName });
        
        if (result == null)
            return null;
            
        return new TempTableInfo
        {
            TempTableName = (string)result.TempTableName,
            UsesSharedTable = (bool)result.UsesSharedTable
        };
    }
    
    /// <inheritdoc/>
    public async Task<int> CountTempTableRecordsAsync(
        string tempTableName,
        Guid? sessionId,
        bool usesSharedTable)
    {
        using var connection = await GetConnectionAsync();
        
        var countSql = usesSharedTable
            ? $"SELECT COUNT(*) FROM `{tempTableName}` WHERE SessionId = @SessionId"
            : $"SELECT COUNT(*) FROM `{tempTableName}`";
        
        return await connection.ExecuteScalarAsync<int>(
            countSql,
            new { SessionId = sessionId?.ToString() });
    }
    
    /// <inheritdoc/>
    public async Task UpdateSessionTableStatusAsync(
        Guid sessionId,
        string tableName,
        int actualRecordCount,
        string status)
    {
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync(@"
            UPDATE SyncSessionTables
            SET ActualRecordCount = @ActualCount,
                Status = @Status
            WHERE SessionId = @SessionId AND TableName = @TableName",
            new
            {
                ActualCount = actualRecordCount,
                Status = status,
                SessionId = sessionId.ToString(),
                TableName = tableName
            });
    }
    
    #endregion
    
    #region Temp Table Operations
    
    /// <inheritdoc/>
    public async Task<int> InsertBatchIntoTempTableAsync(
        string tempTableName,
        bool usesSharedTable,
        Guid sessionId,
        string tableName,
        List<Dictionary<string, object?>> records)
    {
        if (!records.Any())
            return 0;

        using var connection = await GetConnectionAsync();

        // Convert JsonElement to actual values (from HTTP deserialization)
        var convertedRecords = records.Select(EntityReflectionHelper.UnwrapJsonElements).ToList();

        // Filter to valid push columns only (drops IsDirty, SyncSessionId etc.) and normalize
        // keys to PascalCase. GetValidPushColumns returns an OrdinalIgnoreCase set, so
        // Contains() matches camelCase keys from JSON deserialization, and the canonical
        // PascalCase name from the set becomes the dictionary key for correct SQL generation.
        var validColumns = _metadataCache.GetValidPushColumns(tableName);
        convertedRecords = convertedRecords
            .Select(r =>
            {
                var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in r) lookup[kvp.Key] = kvp.Value;
                return validColumns
                    .Where(col => lookup.ContainsKey(col))
                    .ToDictionary(col => col, col => lookup[col]);
            })
            .ToList();

        // Build insert SQL dynamically based on record keys
        var sampleRecord = convertedRecords.First();
        var columns = sampleRecord.Keys.ToList();
        
        string insertSql;
        if (usesSharedTable)
        {
            var columnList = string.Join(", ", new[] { TempTableSessionIdColumn }.Concat(columns.Select(c => $"`{c}`")));
            var valueList = string.Join(", ", new[] { $"@{TempTableSessionIdColumn}" }.Concat(columns.Select(c => $"@{c}")));
            insertSql = $"INSERT INTO `{tempTableName}` ({columnList}) VALUES ({valueList})";
        }
        else
        {
            var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));
            var valueList = string.Join(", ", columns.Select(c => $"@{c}"));
            insertSql = $"INSERT INTO `{tempTableName}` ({columnList}) VALUES ({valueList})";
        }

        // Build parameters for batch insert
        var parameters = convertedRecords.Select(r =>
        {
            var p = new DynamicParameters();
            if (usesSharedTable)
            {
                p.Add(TempTableSessionIdColumn, sessionId.ToString());
            }
            foreach (var kvp in r)
            {
                p.Add(kvp.Key, kvp.Value);
            }
            return p;
        }).ToArray();

        try
        {
            await connection.ExecuteAsync(insertSql, parameters);
        }
        catch (MySqlException ex)
        {
            var errorType = ClassifyMySqlError(ex.Number);
            
            _logger?.LogError(ex,
                "Batch insert failed for temp table {TempTableName}. " +
                "Records: {RecordCount}, Error Type: {ErrorType}, " +
                "MySQL Error Code: {ErrorCode}, Message: {ErrorMessage}",
                tempTableName, convertedRecords.Count, errorType, ex.Number, ex.Message);
            
            throw;
        }

        return convertedRecords.Count;
    }
    
    
    /// <inheritdoc/>
    public async Task<PullBatchResult> GetPullBatchAsync(
        string tempTableName,
        Guid pullSessionId,
        int offset,
        int limit)
    {
        using var connection = await GetConnectionAsync();

        // Get total count
        var countSql = $"SELECT COUNT(*) FROM `{tempTableName}` WHERE {TempTableSessionIdColumn} = @PullSessionId";
        var totalRecords = await connection.ExecuteScalarAsync<int>(
            countSql,
            new { PullSessionId = pullSessionId.ToString() });

        // Extract table name from temp table name to get column list
        // TempPullCustomers -> Customers, TempPull_Customers_abc123 -> Customers
        string tableName;
        if (tempTableName.StartsWith("TempPull_"))
        {
            // Dedicated: TempPull_Customers_sessionId -> Customers
            var parts = tempTableName.Substring("TempPull_".Length).Split('_');
            tableName = parts[0];
        }
        else
        {
            // Shared: TempPullCustomers -> Customers
            tableName = tempTableName.Substring("TempPull".Length);
        }

        // Get columns for server select (includes SyncSessionId)
        var columns = _metadataCache.GetColumnsForServerSelect(tableName);
        var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));

        // Explicitly select only business columns (exclude metadata)
        var batchSql = $@"
            SELECT {columnList}
            FROM `{tempTableName}`
            WHERE {TempTableSessionIdColumn} = @PullSessionId
            ORDER BY Id
            LIMIT @Limit OFFSET @Offset";

        var dynamicRecords = await connection.QueryAsync(
            batchSql,
            new
            {
                PullSessionId = pullSessionId.ToString(),
                Limit = limit,
                Offset = offset
            });

        // Convert dynamic to Dictionary
        var records = dynamicRecords.Select(r =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var prop in (IDictionary<string, object>)r)
            {
                dict[prop.Key] = prop.Value;
            }
            return dict;
        }).ToList();

        var hasMore = (offset + records.Count) < totalRecords;

        return new PullBatchResult
        {
            Records = records,
            HasMore = hasMore,
            TotalRecords = totalRecords
        };
    }
    
    /// <inheritdoc/>
    public async Task<int> DeletePullSessionDataAsync(Guid pullSessionId, string[] tableNames)
    {
        using var connection = await GetConnectionAsync();
        
        int totalDeleted = 0;
        
        foreach (var tableName in tableNames)
        {
            // Check if table exists first
            var tableExists = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*)
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                  AND table_name = @TableName",
                new { TableName = tableName }) > 0;
            
            if (!tableExists)
                continue;
            
            var sql = $"DELETE FROM `{tableName}` WHERE {TempTableSessionIdColumn} = @PullSessionId";
            var deleted = await connection.ExecuteAsync(sql, new { PullSessionId = pullSessionId.ToString() });
            totalDeleted += deleted;
        }
        
        return totalDeleted;
    }
    
    #endregion
    
    #region Queue Processing Operations
    
    /// <inheritdoc/>
    public async Task<List<SessionRecord>> FindReadySessionsAsync(int limit = 100)
    {
        var sql = @"
            SELECT SessionId, SessionType, Status, SyncVersion, 
                   CreatedAtUtc, LastActivityUtc, CommittedAtUtc, ErrorMessage
            FROM SessionRecords
            WHERE Status = 'Ready'
            ORDER BY CreatedAtUtc
            LIMIT @Limit";

        using var connection = await GetConnectionAsync();
        var sessions = await connection.QueryAsync<SessionRecord>(sql, new { Limit = limit });
        return sessions.ToList();
    }
    
    /// <inheritdoc/>
    public async Task<List<SessionTableInfo>> GetSessionTableDetailsAsync(Guid sessionId)
    {
        var sql = @"
            SELECT TableName, TempTableName, ProcessingPriority AS Priority, UsesSharedTable
            FROM SyncSessionTables
            WHERE SessionId = @SessionId
            ORDER BY ProcessingPriority";

        using var connection = await GetConnectionAsync();
        var tables = await connection.QueryAsync<SessionTableInfo>(
            sql,
            new { SessionId = sessionId.ToString() });
        return tables.ToList();
    }

    /// <inheritdoc/>
    public async Task<int> UpsertFromTempTableAsync(
        string tableName,
        string tempTableName,
        bool usesSharedTable,
        Guid sessionId,
        IDbTransaction transaction)
    {
        // Get column list - includes business + IsDeleted + ModifiedByUserId + ModifiedAtUtc
        var columns = _metadataCache.GetColumnsForServerUpsert(tableName);
        var insertColumnList = string.Join(", ", columns.Select(c => $"`{c}`"));
        // SELECT: preserve client ModifiedAtUtc when provided; fall back to server time if null.
        // All other columns selected as-is.
        var selectColumnList = string.Join(", ", columns.Select(c =>
            c == "ModifiedAtUtc"
                ? "COALESCE(`ModifiedAtUtc`, UTC_TIMESTAMP(6))"
                : $"`{c}`"));
        var updateList = string.Join(", ", columns.Where(c => c != "Id").Select(c => $"`{c}` = VALUES(`{c}`)"));

        // Build upsert SQL
        // ModifiedAtUtc: client value preserved when set; falls back to UTC_TIMESTAMP(6) if null.
        // SessionRecord.CommittedAtUtc is the authoritative server-side processing timestamp.
        var sql = $@"
        INSERT INTO `{tableName}` ({insertColumnList}, SyncSessionId)
        SELECT {selectColumnList}, @SessionId
        FROM `{tempTableName}`
        {(usesSharedTable ? "WHERE SessionId = @SessionId" : "")}
        ON DUPLICATE KEY UPDATE
            {updateList},
            SyncSessionId = VALUES(SyncSessionId)";

        var rowsAffected = await transaction.Connection!.ExecuteAsync(
            sql,
            new { SessionId = sessionId.ToString() },
            transaction);

        return rowsAffected;
    }


    /// <inheritdoc/>
    public async Task UpdateSessionStatusAsync(
        Guid sessionId,
        string status,
        IDbTransaction? transaction = null,
        long? syncVersion = null,
        string? errorMessage = null,
        int? totalRows = null,
        string? rowCountsJson = null)
    {
        var sql = @"
            UPDATE SessionRecords
            SET Status = @Status,
                CommittedAtUtc = CASE WHEN @Status IN ('Committed', 'Completed') THEN UTC_TIMESTAMP(6) ELSE CommittedAtUtc END,
                ErrorMessage = COALESCE(@ErrorMessage, ErrorMessage),
                TotalRows = COALESCE(@TotalRows, TotalRows),
                RowCountsJson = COALESCE(@RowCountsJson, RowCountsJson),
                LastActivityUtc = UTC_TIMESTAMP(6)
            WHERE SessionId = @SessionId";

        var parameters = new
        {
            SessionId = sessionId.ToString(),
            Status = status,
            ErrorMessage = errorMessage,
            TotalRows = totalRows,
            RowCountsJson = rowCountsJson,
        };

        if (transaction != null)
        {
            // Transactional path
            await transaction.Connection!.ExecuteAsync(sql, parameters, transaction);
        }
        else
        {
            // Non-transactional path
            using var connection = await GetConnectionAsync();
            await connection.ExecuteAsync(sql, parameters);
        }
    }
    
    /// <inheritdoc/>
    public async Task<int> CountRecordsFromSessionsAsync(string tableName, IEnumerable<Guid> sessionIds, Guid? tenantId = null)
    {
        var sessionIdList = sessionIds.Select(id => id.ToString()).ToList();
        
        if (!sessionIdList.Any())
            return 0;
        
        var sql = $@"
            SELECT COUNT(*)
            FROM `{tableName}`
            WHERE SyncSessionId IN @SessionIds";
        
        // Multi-tenant filtering
        if (_metadataCache.IsMultiTenant(tableName) && tenantId.HasValue)
        {
            sql += " AND TenantId = @TenantId";
        }
        
        using var connection = await GetConnectionAsync();
        var count = await connection.ExecuteScalarAsync<int>(sql, new 
        { 
            SessionIds = sessionIdList,
            TenantId = tenantId?.ToString()
        });
        
        return count;
    }
    
    /// <inheritdoc/>
    public async Task<int> SnapshotRecordsForPullAsync(
        string tempTableName,
        string sourceTableName,
        IEnumerable<Guid> sessionIds,
        Guid pullSessionId,
        bool usesSharedTable,
        Guid? tenantId = null)
    {
        // Convert session IDs to list for query
        var sessionIdList = sessionIds.Select(id => id.ToString()).ToList();
        
        if (!sessionIdList.Any())
            return 0;
        
        // Get all columns for server SELECT (business + sync columns including SyncSessionId)
        var allColumns = _metadataCache.GetColumnsForServerSelect(sourceTableName);
        var columnList = string.Join(", ", allColumns.Select(c => $"`{c}`"));
        
        // Multi-tenant filtering
        var tenantFilter = "";
        if (_metadataCache.IsMultiTenant(sourceTableName) && tenantId.HasValue)
        {
            tenantFilter = " AND TenantId = @TenantId";
        }
        
        // Build INSERT SELECT statement
        // For shared tables, include PullSessionId column
        // For dedicated tables, PullSessionId column doesn't exist (not needed for isolation)
        string sql;
        
        if (usesSharedTable)
        {
            sql = $@"
                INSERT INTO `{tempTableName}` 
                ({TempTableSessionIdColumn}, {columnList})
                SELECT 
                    @PullSessionId,
                    {columnList}
                FROM `{sourceTableName}`
                WHERE SyncSessionId IN @SessionIds{tenantFilter}";
        }
        else
        {
            // Dedicated table - no PullSessionId column needed
            sql = $@"
                INSERT INTO `{tempTableName}` 
                ({columnList})
                SELECT 
                    {columnList}
                FROM `{sourceTableName}`
                WHERE SyncSessionId IN @SessionIds{tenantFilter}";
        }
        
        using var connection = await GetConnectionAsync();
        var rowsInserted = await connection.ExecuteAsync(sql, new
        {
            PullSessionId = pullSessionId.ToString(),
            SessionIds = sessionIdList,
            TenantId = tenantId?.ToString()
        });
        
        return rowsInserted;
    }
    
    #endregion
    
    #region Utility Operations

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SyncSessionSummary>> GetRecentSessionsAsync(Guid? tenantId, int limit = 50)
    {
        using var connection = await GetConnectionAsync();

        const string sql = @"
            SELECT s.SessionId, s.TenantId, s.DeviceId, s.SessionType, s.SyncVersion, s.CommittedAtUtc,
                   st.TableName, st.ActualRecordCount
            FROM SessionRecords s
            LEFT JOIN SyncSessionTables st ON st.SessionId = s.SessionId
            WHERE s.Status = 'Committed'
              AND (@TenantId IS NULL OR s.TenantId = @TenantId)
            ORDER BY s.SyncVersion DESC
            LIMIT @Limit";

        var rows = await connection.QueryAsync<(Guid SessionId, Guid? TenantId, Guid? DeviceId, string SessionType,
            long SyncVersion, DateTime CommittedAtUtc, string? TableName, int? ActualRecordCount)>(
            sql, new { TenantId = tenantId?.ToString(), Limit = limit });

        return rows
            .GroupBy(r => r.SessionId)
            .Select(g =>
            {
                var first = g.First();
                return new SyncSessionSummary
                {
                    SessionId = first.SessionId,
                    TenantId = first.TenantId,
                    DeviceId = first.DeviceId,
                    SessionType = first.SessionType,
                    SyncVersion = first.SyncVersion,
                    CommittedAtUtc = first.CommittedAtUtc,
                    Tables = g
                        .Where(r => r.TableName != null)
                        .Select(r => new SyncSessionTableSummary
                        {
                            TableName = r.TableName!,
                            RecordCount = r.ActualRecordCount ?? 0
                        })
                        .ToList()
                };
            })
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SyncSessionTable>> GetSessionTablesAsync(Guid sessionId)
    {
        const string sql = @"
            SELECT
                SessionId, TableName, TempTableName, UsesSharedTable, Status,
                EstimatedRecordCount, ActualRecordCount, ProcessingPriority
            FROM SyncSessionTables
            WHERE SessionId = @SessionId
            ORDER BY ProcessingPriority";

        using var connection = await GetConnectionAsync();
        var results = await connection.QueryAsync<SyncSessionTable>(sql, new { SessionId = sessionId.ToString() });
        return results.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<int> ExecuteRawSqlAsync(string sql)
    {
        using var connection = await GetConnectionAsync();
        return await connection.ExecuteAsync(sql);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetTableColumnsAsync(string tableName)
    {
        const string sql = @"
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @TableName";

        using var connection = await GetConnectionAsync();
        var columns = await connection.QueryAsync<string>(sql, new { TableName = tableName });
        return columns.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task EnsureSharedTempTablesAsync()
    {
        int tablesCreated = 0, columnsAdded = 0;
        var tableCount = _config.Tables.Count;

        _logger?.LogInformation(
            "EnsureSharedTempTables: Checking shared temp tables for {Count} registered entity table(s)",
            tableCount);

        using var connection = await GetConnectionAsync();

        foreach (var kvp in _config.Tables)
        {
            var tableName = kvp.Key;

            // Get entity table column definitions from INFORMATION_SCHEMA
            var entityColDefs = await QueryColumnDefinitionsAsync(connection, tableName);
            if (entityColDefs.Count == 0)
            {
                _logger?.LogWarning(
                    "EnsureSharedTempTables: Entity table '{Table}' not found — skipping temp table creation",
                    tableName);
                continue;
            }

            var colLookup = entityColDefs.ToDictionary(
                c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

            // --- Push temp table ---
            var pushCols = _metadataCache.GetColumnsForServerUpsert(tableName);
            var (pc, pa) = await EnsureSingleTempTableAsync(
                connection, $"TempPush{tableName}", colLookup, pushCols, isPush: true);
            tablesCreated += pc;
            columnsAdded += pa;

            // --- Pull temp table ---
            var pullCols = _metadataCache.GetColumnsForServerSelect(tableName);
            var (lc, la) = await EnsureSingleTempTableAsync(
                connection, $"TempPull{tableName}", colLookup, pullCols, isPush: false);
            tablesCreated += lc;
            columnsAdded += la;
        }

        if (tablesCreated > 0 || columnsAdded > 0)
        {
            _logger?.LogInformation(
                "EnsureSharedTempTables complete: {Created} table(s) created, {Added} column(s) added",
                tablesCreated, columnsAdded);
        }
        else
        {
            _logger?.LogInformation("EnsureSharedTempTables: all temp tables up to date");
        }
    }

    /// <summary>
    /// Ensures a single shared temp table exists with the required columns.
    /// Creates the table if missing; adds missing columns if it already exists.
    /// </summary>
    /// <returns>Tuple of (tablesCreated, columnsAdded).</returns>
    private async Task<(int TablesCreated, int ColumnsAdded)> EnsureSingleTempTableAsync(
        IDbConnection connection,
        string tempTableName,
        Dictionary<string, TempTableColumnDef> entityColLookup,
        IEnumerable<string> requiredColumns,
        bool isPush)
    {
        var existingColumns = await QueryColumnDefinitionsAsync(connection, tempTableName);

        if (existingColumns.Count == 0)
        {
            // Table doesn't exist — create it
            var colCount = requiredColumns.Count();
            await CreateSharedTempTableAsync(connection, tempTableName, entityColLookup, requiredColumns, isPush);
            _logger?.LogInformation(
                "EnsureSharedTempTables: Created {TempTable} ({ColumnCount} columns)",
                tempTableName, colCount);
            return (1, 0);
        }

        // Table exists — check for missing columns (schema drift)
        var existingNames = new HashSet<string>(
            existingColumns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var colName in requiredColumns)
        {
            if (existingNames.Contains(colName))
                continue;

            if (!entityColLookup.TryGetValue(colName, out var colDef))
            {
                _logger?.LogWarning(
                    "EnsureSharedTempTables: Column '{Column}' required by metadata but not found in entity table — skipping for {TempTable}",
                    colName, tempTableName);
                continue;
            }

            // For push tables, override ModifiedAtUtc to be nullable (client may omit;
            // UpsertFromTempTableAsync uses COALESCE to fill in server time).
            var nullable = isPush && colName.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase)
                ? "NULL"
                : (colDef.IsNullable ? "NULL" : "NOT NULL");

            var alterSql = $"ALTER TABLE `{tempTableName}` ADD COLUMN `{colName}` {colDef.ColumnType} {nullable}";
            await connection.ExecuteAsync(alterSql);

            _logger?.LogInformation(
                "EnsureSharedTempTables: Added column '{Column}' ({Type}) to {TempTable}",
                colName, colDef.ColumnType, tempTableName);
            added++;
        }

        // Detect stale columns — exist in temp table but not required by metadata cache.
        // These are harmless (INSERT SQL is driven by metadata, not table schema) but worth
        // logging so operators know about drift. Do NOT auto-drop — destructive on misconfiguration.
        var requiredSet = new HashSet<string>(requiredColumns, StringComparer.OrdinalIgnoreCase);
        // Infrastructure columns are expected in temp tables but not in the metadata cache
        requiredSet.Add(TempTableSessionIdColumn);
        if (isPush) requiredSet.Add("SequenceNumber");

        var staleColumns = existingColumns
            .Select(c => c.Name)
            .Where(name => !requiredSet.Contains(name))
            .ToList();

        if (staleColumns.Count > 0)
        {
            _logger?.LogWarning(
                "EnsureSharedTempTables: {TempTable} has {Count} column(s) not in metadata cache (harmless but stale): {Columns}",
                tempTableName, staleColumns.Count, string.Join(", ", staleColumns));
        }

        if (added == 0 && staleColumns.Count == 0)
        {
            _logger?.LogInformation("EnsureSharedTempTables: {TempTable} is up to date", tempTableName);
        }

        return (0, added);
    }

    /// <summary>
    /// Creates a shared temp table from scratch, deriving column types from the entity table.
    /// </summary>
    private async Task CreateSharedTempTableAsync(
        IDbConnection connection,
        string tempTableName,
        Dictionary<string, TempTableColumnDef> entityColLookup,
        IEnumerable<string> requiredColumns,
        bool isPush)
    {
        var parts = new List<string>();

        if (isPush)
        {
            parts.Add("SequenceNumber INT AUTO_INCREMENT");
            parts.Add($"{TempTableSessionIdColumn} CHAR(36) NOT NULL");
        }
        else
        {
            parts.Add($"{TempTableSessionIdColumn} CHAR(36) NOT NULL");
        }

        foreach (var colName in requiredColumns)
        {
            if (!entityColLookup.TryGetValue(colName, out var colDef))
            {
                _logger?.LogWarning(
                    "EnsureSharedTempTables: Column '{Column}' not in entity table — omitting from {TempTable}",
                    colName, tempTableName);
                continue;
            }

            // Push: ModifiedAtUtc nullable (client may omit, server fills via COALESCE).
            // Everything else mirrors entity nullability.
            var nullable = isPush && colName.Equals("ModifiedAtUtc", StringComparison.OrdinalIgnoreCase)
                ? "NULL"
                : (colDef.IsNullable ? "NULL" : "NOT NULL");

            parts.Add($"`{colName}` {colDef.ColumnType} {nullable}");
        }

        // Primary key and indexes
        if (isPush)
        {
            parts.Add($"PRIMARY KEY (SequenceNumber, {TempTableSessionIdColumn})");
        }
        else
        {
            parts.Add($"PRIMARY KEY ({TempTableSessionIdColumn}, Id)");
        }

        parts.Add($"INDEX IX_{tempTableName}_Session ({TempTableSessionIdColumn})");

        var sql = $"CREATE TABLE IF NOT EXISTS `{tempTableName}` (\n  {string.Join(",\n  ", parts)}\n) ENGINE=InnoDB";
        await connection.ExecuteAsync(sql);
    }

    /// <summary>
    /// Queries INFORMATION_SCHEMA.COLUMNS for a table's column definitions.
    /// Returns empty list if the table does not exist.
    /// </summary>
    private async Task<List<TempTableColumnDef>> QueryColumnDefinitionsAsync(
        IDbConnection connection, string tableName)
    {
        const string sql = @"
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @TableName
            ORDER BY ORDINAL_POSITION";

        var rows = await connection.QueryAsync<dynamic>(sql, new { TableName = tableName });
        return rows.Select(r => new TempTableColumnDef(
            (string)r.COLUMN_NAME,
            (string)r.COLUMN_TYPE,
            ((string)r.IS_NULLABLE) == "YES"
        )).ToList();
    }

    /// <summary>
    /// Column metadata used when building shared temp table DDL.
    /// </summary>
    private record TempTableColumnDef(string Name, string ColumnType, bool IsNullable);
    
    #region Direct Write Operations (Session 28a)

    /// <inheritdoc/>
    public async Task<int> UpsertDirectAsync(string tableName, List<object> records, Guid sessionId, IDbTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));

        if (records == null || records.Count == 0)
            throw new ArgumentException("Records list cannot be null or empty", nameof(records));

        // Validate all records are same type and implement ISyncEntity
        var firstRecord = records[0];
        var recordType = firstRecord.GetType();

        foreach (var record in records)
        {
            if (record.GetType() != recordType)
                throw new ArgumentException("All records must be of the same type", nameof(records));

            if (record is not ISyncEntity)
                throw new ArgumentException($"All records must implement ISyncEntity. Type {recordType.Name} does not.", nameof(records));
        }

        _logger.LogDebug(
            "UpsertDirectAsync: Upserting {RecordCount} records to {TableName}, SessionId={SessionId}",
            records.Count,
            tableName,
            sessionId);

        // Get columns for direct upsert (includes SyncSessionId — entity already has it set)
        var columns = _metadataCache.GetColumnsForDirectUpsert(tableName).ToList();

        var totalWritten = 0;
        const int batchSize = 500; // MySQL parameter limit is ~65K, stay well below

        // Process in batches to avoid parameter limits
        for (int i = 0; i < records.Count; i += batchSize)
        {
            var batch = records.Skip(i).Take(batchSize).ToList();
            
            // Build VALUES clause: (?, ?, ?), (?, ?, ?), ...
            var valuesClause = BuildValuesClause(batch.Count, columns.Count);
            
            // Build complete upsert SQL
            var sql = BuildUpsertSql(tableName, columns, valuesClause);
            
            // Build parameter object from batch records
            var parameters = BuildUpsertParameters(batch, columns);
            
            int written;
            if (transaction != null)
            {
                written = await transaction.Connection!.ExecuteAsync(sql, parameters, transaction);
            }
            else
            {
                using var connection = await GetConnectionAsync();
                written = await connection.ExecuteAsync(sql, parameters);
            }
            totalWritten += written;

            _logger.LogDebug(
                "UpsertDirectAsync: Batch {BatchNum} wrote {RecordCount} records to {TableName}",
                (i / batchSize) + 1,
                written,
                tableName);
        }

        _logger.LogInformation(
            "UpsertDirectAsync: Successfully wrote {TotalRecords} records to {TableName}, SessionId={SessionId}",
            totalWritten,
            tableName,
            sessionId);

        return totalWritten;
    }

    /// <inheritdoc/>
    public async Task<int> SoftDeleteDirectAsync(
        string tableName,
        Guid id,
        string userId,
        Guid sessionId,
        Guid? tenantId,
        IDbTransaction? transaction = null)
    {
        var tenantClause = tenantId.HasValue ? " AND `TenantId` = @TenantId" : string.Empty;

        var sql = $@"
            UPDATE `{tableName}`
            SET `IsDeleted`        = 1,
                `ModifiedByUserId` = @UserId,
                `ModifiedAtUtc`    = UTC_TIMESTAMP(6),
                `SyncSessionId`    = @SessionId
            WHERE `Id` = @Id{tenantClause}";

        var parameters = new DynamicParameters();
        parameters.Add("@UserId",    userId);
        parameters.Add("@SessionId", sessionId.ToString());
        parameters.Add("@Id",        id.ToString());
        if (tenantId.HasValue)
            parameters.Add("@TenantId", tenantId.Value.ToString());

        if (transaction != null)
            return await transaction.Connection!.ExecuteAsync(sql, parameters, transaction);

        using var connection = await GetConnectionAsync();
        return await connection.ExecuteAsync(sql, parameters);
    }

    /// <summary>
    /// Builds the core INSERT...ON DUPLICATE KEY UPDATE SQL statement for direct upserts.
    /// </summary>
    /// <param name="tableName">Target business table name</param>
    /// <param name="columns">List of column names to include in the upsert</param>
    /// <param name="sourceClause">VALUES clause with parameter placeholders</param>
    /// <returns>Complete MySQL upsert SQL statement</returns>
    /// <remarks>
    /// Used by UpsertDirectAsync for direct write operations.
    /// UpsertFromTempTableAsync uses its own inline SQL due to COALESCE/SyncSessionId differences.
    /// </remarks>
    private string BuildUpsertSql(string tableName, IReadOnlyList<string> columns, string sourceClause)
    {
        var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));
        
        // Build UPDATE clause: col1 = VALUES(col1), col2 = VALUES(col2), ...
        // Exclude Id — updating PK to itself is unnecessary
        var updateAssignments = columns
            .Where(col => col != "Id")
            .Select(col => $"`{col}` = VALUES(`{col}`)")
            .ToList();
        
        var updateClause = string.Join(", ", updateAssignments);

        var sql = $@"
            INSERT INTO `{tableName}` ({columnList})
            {sourceClause}
            ON DUPLICATE KEY UPDATE
                {updateClause}";

        return sql;
    }

    /// <summary>
    /// Builds a VALUES clause for direct upsert with MySQL parameter placeholders.
    /// </summary>
    /// <param name="recordCount">Number of records in the batch</param>
    /// <param name="columnCount">Number of columns per record</param>
    /// <returns>VALUES clause string like: VALUES (?, ?, ?), (?, ?, ?), ...</returns>
    /// <remarks>
    /// For 2 records with 3 columns each:
    /// VALUES (@p0_Col1, @p0_Col2, @p0_Col3), (@p1_Col1, @p1_Col2, @p1_Col3)
    /// </remarks>
    private string BuildValuesClause(int recordCount, int columnCount)
    {
        var valueSets = new List<string>();
        var paramIndex = 0;

        for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            var placeholders = new List<string>();
            for (int colIndex = 0; colIndex < columnCount; colIndex++)
            {
                placeholders.Add($"@p{paramIndex}");
                paramIndex++;
            }

            valueSets.Add($"({string.Join(", ", placeholders)})");
        }

        return $"VALUES {string.Join(", ", valueSets)}";
    }

    /// <summary>
    /// Builds a flat parameter object for Dapper from a batch of records.
    /// </summary>
    /// <param name="records">Batch of entities to convert to parameters</param>
    /// <param name="columns">Column names in order</param>
    /// <returns>DynamicParameters object with flattened parameter values</returns>
    /// <remarks>
    /// Dapper expects a flat list of values matching the ? placeholders in the VALUES clause.
    /// For 2 records with columns [Id, Name, Email]:
    /// Returns: [record0.Id, record0.Name, record0.Email, record1.Id, record1.Name, record1.Email]
    /// </remarks>
    private object BuildUpsertParameters(List<object> records, IReadOnlyList<string> columns)
    {
        var parameters = new DynamicParameters();
        var flatValues = new List<object?>();

        foreach (var record in records)
        {
            var recordType = record.GetType();

            foreach (var columnName in columns)
            {
                var property = recordType.GetProperty(columnName);
                if (property == null)
                    throw new InvalidOperationException($"Property {columnName} not found on type {recordType.Name}");

                var value = property.GetValue(record);
                
                // Convert Guid to string for MySQL VARCHAR(36) columns
                if (value is Guid guidValue)
                    value = guidValue.ToString();
                
                flatValues.Add(value);
            }
        }

        // Add all values as positional parameters
        for (int i = 0; i < flatValues.Count; i++)
        {
            parameters.Add($"@p{i}", flatValues[i]);
        }

        return parameters;
    }

    #endregion

    #region Data Query Operations

    /// <inheritdoc />
    public async Task<Dictionary<string, object?>?> GetByIdAsync(
        string tableName, Guid id, Guid? tenantId = null)
    {
        var columns = EntityReflectionHelper.GetColumnsForServerSelect(tableName);
        var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));

        var tenantClause = tenantId.HasValue ? " AND `TenantId` = @TenantId" : string.Empty;
        var sql = $"SELECT {columnList} FROM `{tableName}` WHERE `Id` = @Id{tenantClause} LIMIT 1";

        var parameters = new DynamicParameters();
        parameters.Add("@Id", id.ToString());
        if (tenantId.HasValue)
            parameters.Add("@TenantId", tenantId.Value.ToString());

        using var connection = await GetConnectionAsync();
        var row = await connection.QueryFirstOrDefaultAsync(sql, parameters);
        if (row == null) return null;

        // Convert Dapper's dynamic row to Dictionary<string, object?>
        var dict = new Dictionary<string, object?>();
        foreach (var prop in (IDictionary<string, object>)row)
            dict[prop.Key] = prop.Value is DBNull ? null : prop.Value;

        return dict;
    }

    /// <inheritdoc />
    public async Task<DataQueryResult> QueryAsync(
        string tableName, DataQuery query, Guid? tenantId = null)
    {
        var columns = EntityReflectionHelper.GetColumnsForServerSelect(tableName);
        var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));
        var validColumns = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Clamp limit
        const int maxLimit = 500;
        var limit = Math.Min(Math.Max(query.Limit, 1), maxLimit);
        var offset = Math.Max(query.Offset, 0);

        var parameters = new DynamicParameters();
        var whereClauses = new List<string>();
        var paramIndex = 0;

        // Enforce IsDeleted filter unless IncludeDeleted
        if (!query.IncludeDeleted)
            whereClauses.Add("`IsDeleted` = 0");

        // Enforce tenant isolation
        if (tenantId.HasValue)
        {
            whereClauses.Add($"`TenantId` = @pTenant");
            parameters.Add("@pTenant", tenantId.Value.ToString());
        }

        // User-supplied filters
        if (query.Filters != null)
        {
            foreach (var filter in query.Filters)
            {
                if (!validColumns.Contains(filter.Column))
                    throw new ArgumentException($"Unknown column '{filter.Column}' in table '{tableName}'.");

                var paramName = $"@p{paramIndex++}";
                var col = $"`{filter.Column}`";

                switch (filter.Operator)
                {
                    case FilterOperator.Equals:
                        if (filter.Value == null)
                            whereClauses.Add($"{col} IS NULL");
                        else
                        {
                            whereClauses.Add($"{col} = {paramName}");
                            parameters.Add(paramName, filter.Value);
                        }
                        break;

                    case FilterOperator.NotEquals:
                        if (filter.Value == null)
                            whereClauses.Add($"{col} IS NOT NULL");
                        else
                        {
                            whereClauses.Add($"{col} != {paramName}");
                            parameters.Add(paramName, filter.Value);
                        }
                        break;

                    case FilterOperator.Contains:
                        whereClauses.Add($"{col} LIKE {paramName}");
                        parameters.Add(paramName, $"%{filter.Value}%");
                        break;

                    case FilterOperator.StartsWith:
                        whereClauses.Add($"{col} LIKE {paramName}");
                        parameters.Add(paramName, $"{filter.Value}%");
                        break;

                    case FilterOperator.GreaterThan:
                        whereClauses.Add($"{col} > {paramName}");
                        parameters.Add(paramName, filter.Value);
                        break;

                    case FilterOperator.LessThan:
                        whereClauses.Add($"{col} < {paramName}");
                        parameters.Add(paramName, filter.Value);
                        break;

                    case FilterOperator.GreaterThanOrEqual:
                        whereClauses.Add($"{col} >= {paramName}");
                        parameters.Add(paramName, filter.Value);
                        break;

                    case FilterOperator.LessThanOrEqual:
                        whereClauses.Add($"{col} <= {paramName}");
                        parameters.Add(paramName, filter.Value);
                        break;

                    case FilterOperator.In:
                        var inValues = filter.Value as IEnumerable<object?>;
                        if (inValues == null)
                            throw new ArgumentException($"In operator requires a collection value for column '{filter.Column}'.");

                        var inParams = new List<string>();
                        foreach (var val in inValues)
                        {
                            var inParam = $"@p{paramIndex++}";
                            inParams.Add(inParam);
                            parameters.Add(inParam, val);
                        }
                        whereClauses.Add($"{col} IN ({string.Join(", ", inParams)})");
                        break;

                    default:
                        throw new ArgumentException($"Unsupported filter operator: {filter.Operator}");
                }
            }
        }

        var whereClause = whereClauses.Count > 0
            ? "WHERE " + string.Join(" AND ", whereClauses)
            : string.Empty;

        // Order
        var orderColumn = "ModifiedAtUtc";
        if (!string.IsNullOrWhiteSpace(query.OrderBy) && validColumns.Contains(query.OrderBy))
            orderColumn = query.OrderBy;
        var orderDirection = query.OrderDescending ? "DESC" : "ASC";
        var orderClause = $"ORDER BY `{orderColumn}` {orderDirection}";

        // Count query
        var countSql = $"SELECT COUNT(*) FROM `{tableName}` {whereClause}";
        using var connection = await GetConnectionAsync();
        var total = await connection.ExecuteScalarAsync<int>(countSql, parameters);

        // Data query
        var dataSql = $"SELECT {columnList} FROM `{tableName}` {whereClause} {orderClause} LIMIT {limit} OFFSET {offset}";
        var rows = await connection.QueryAsync(dataSql, parameters);

        var records = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var prop in (IDictionary<string, object>)row)
                dict[prop.Key] = prop.Value is DBNull ? null : prop.Value;
            records.Add(dict);
        }

        return new DataQueryResult
        {
            Table = tableName,
            Records = records,
            Total = total,
            Offset = offset,
            Limit = limit
        };
    }

    #endregion
    
    /// <summary>
    /// Maps a MySQL error code to a descriptive error category string.
    /// </summary>
    private static string ClassifyMySqlError(int errorCode)
    {
        return errorCode switch
        {
            1062 => "DUPLICATE_KEY",           // Duplicate entry for PRIMARY KEY or UNIQUE
            1406 => "DATA_TOO_LONG",           // Data too long for column
            1048 => "NOT_NULL_VIOLATION",      // Column cannot be null
            1452 => "FOREIGN_KEY_VIOLATION",   // Cannot add or update child row
            1054 => "UNKNOWN_COLUMN",          // Unknown column in field list
            1146 => "TABLE_NOT_FOUND",         // Table doesn't exist
            2002 => "CONNECTION_REFUSED",      // Can't connect to MySQL server
            2003 => "CONNECTION_ERROR",        // Can't connect to MySQL server on port
            2006 => "CONNECTION_LOST",         // MySQL server has gone away
            2013 => "CONNECTION_TIMEOUT",      // Lost connection to MySQL server during query
            _ => "UNKNOWN_ERROR"
        };
    }
    
    #endregion

    #region Seed Snapshots

    /// <inheritdoc/>
    public async Task<SeedSnapshot?> FindSeedSnapshotAsync(Guid deviceId, Guid tenantId)
    {
        using var connection = await GetConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<SeedSnapshot>(
            @"SELECT SeedId, DeviceId, TenantId, Status, CreatedAtUtc, LastActivityUtc
              FROM SeedSnapshots
              WHERE DeviceId = @DeviceId AND TenantId = @TenantId",
            new { DeviceId = deviceId.ToString(), TenantId = tenantId.ToString() });
    }

    /// <inheritdoc/>
    public async Task InsertSeedSnapshotAsync(Guid seedId, Guid deviceId, Guid tenantId)
    {
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO SeedSnapshots (SeedId, DeviceId, TenantId, Status, CreatedAtUtc, LastActivityUtc)
              VALUES (@SeedId, @DeviceId, @TenantId, 'Active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))",
            new { SeedId = seedId.ToString(), DeviceId = deviceId.ToString(), TenantId = tenantId.ToString() });
    }

    /// <inheritdoc/>
    public async Task UpdateSeedSnapshotActivityAsync(Guid seedId)
    {
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE SeedSnapshots SET LastActivityUtc = UTC_TIMESTAMP(6) WHERE SeedId = @SeedId",
            new { SeedId = seedId.ToString() });
    }

    /// <inheritdoc/>
    public async Task UpdateSeedSnapshotStatusAsync(Guid seedId, string status)
    {
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE SeedSnapshots SET Status = @Status, LastActivityUtc = UTC_TIMESTAMP(6) WHERE SeedId = @SeedId",
            new { SeedId = seedId.ToString(), Status = status });
    }

    /// <inheritdoc/>
    public async Task DeleteSeedSnapshotAsync(Guid seedId)
    {
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM SeedSnapshots WHERE SeedId = @SeedId",
            new { SeedId = seedId.ToString() });
    }

    /// <inheritdoc/>
    public async Task CreateSeedSnapshotTableAsync(string snapTableName, string sourceTableName, Guid? tenantId)
    {
        // Snapshot creation can be slow for large tables — use a long timeout (10 minutes)
        const int snapshotTimeoutSeconds = 600;
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(
            $"DROP TABLE IF EXISTS `{snapTableName}`",
            commandTimeout: snapshotTimeoutSeconds);
        if (tenantId.HasValue)
            await connection.ExecuteAsync(
                $"CREATE TABLE `{snapTableName}` AS SELECT * FROM `{sourceTableName}` WHERE TenantId = @TenantId",
                new { TenantId = tenantId.Value.ToString() },
                commandTimeout: snapshotTimeoutSeconds);
        else
            await connection.ExecuteAsync(
                $"CREATE TABLE `{snapTableName}` AS SELECT * FROM `{sourceTableName}`",
                commandTimeout: snapshotTimeoutSeconds);

        // CREATE TABLE AS SELECT does not copy indexes — add one on Id for keyset pagination
        await connection.ExecuteAsync(
            $"ALTER TABLE `{snapTableName}` ADD INDEX idx_id (Id)",
            commandTimeout: snapshotTimeoutSeconds);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<SeedSnapshot>> FindOrphanedSeedSnapshotsAsync(DateTime cutoff)
    {
        using var connection = await GetConnectionAsync();
        return await connection.QueryAsync<SeedSnapshot>(
            @"SELECT SeedId, DeviceId, TenantId, Status, CreatedAtUtc, LastActivityUtc
              FROM SeedSnapshots
              WHERE Status = 'Active' AND LastActivityUtc < @Cutoff",
            new { Cutoff = cutoff });
    }

    /// <inheritdoc/>
    public async Task<List<string>> FindSeedSnapshotTableNamesAsync(Guid seedId)
    {
        var suffix = seedId.ToString("N");
        using var connection = await GetConnectionAsync();
        var tables = await connection.QueryAsync<string>(
            @"SELECT TABLE_NAME FROM information_schema.TABLES
              WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME LIKE CONCAT('SeedSnap_%_', @Suffix)",
            new { Suffix = suffix });
        return tables.AsList();
    }

    /// <inheritdoc/>
    public async Task<List<Dictionary<string, object?>>> GetSeedSnapshotBatchAsync(
        string snapTableName, int offset, int limit)
    {
        using var connection = await GetConnectionAsync();
        var rows = await connection.QueryAsync(
            $"SELECT * FROM `{snapTableName}` ORDER BY Id LIMIT @Limit OFFSET @Offset",
            new { Limit = limit, Offset = offset },
            commandTimeout: 300);
        return rows
            .Select(r => ((IDictionary<string, object?>)r)
                .ToDictionary(kv => kv.Key, kv => kv.Value))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<List<Dictionary<string, object?>>> GetSeedSnapshotBatchAfterIdAsync(
        string snapTableName, string afterId, int limit)
    {
        using var connection = await GetConnectionAsync();
        var rows = await connection.QueryAsync(
            $"SELECT * FROM `{snapTableName}` WHERE Id > @AfterId ORDER BY Id LIMIT @Limit",
            new { AfterId = afterId, Limit = limit },
            commandTimeout: 300);
        return rows
            .Select(r => ((IDictionary<string, object?>)r)
                .ToDictionary(kv => kv.Key, kv => kv.Value))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<int> GetSeedSnapshotCountAsync(string snapTableName)
    {
        using var connection = await GetConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM `{snapTableName}`",
            commandTimeout: 300);
    }

    /// <inheritdoc/>
    public async Task DropSeedSnapshotTableAsync(string snapTableName)
    {
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync($"DROP TABLE IF EXISTS `{snapTableName}`");
    }

    #endregion
}

