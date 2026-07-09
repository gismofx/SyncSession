using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using SyncSession.Core.Attributes;
using SyncSession.Core.DTOs;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;
using SyncSession.Server.Models;

namespace SyncSession.Server.Database;

/// <summary>
/// SQLite implementation of <see cref="IServerDatabase"/> for development, testing, and embedded scenarios.
/// </summary>
/// <remarks>
/// <para>Recommended for development, CI/CD pipelines, demos, and single-user embedded applications.</para>
/// <para>Not recommended for production multi-client deployments — use <see cref="MySqlServerDatabase"/> instead.
/// SQLite has limited concurrent write support, single-file storage, and requires TEXT conversion for GUIDs and dates.</para>
/// </remarks>
public class SqliteServerDatabase : IServerDatabase
{
    private const string TempTableSessionIdColumn = "SessionId";

    private readonly SqliteConnection _connection;
    private readonly ITableMetadataCache _metadataCache;
    private readonly ServerSyncConfiguration _config;

    public SqliteServerDatabase(
        SqliteConnection connection,
        ITableMetadataCache metadataCache,
        ServerSyncConfiguration config)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _metadataCache = metadataCache ?? throw new ArgumentNullException(nameof(metadataCache));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc/>
    public async Task<IDbConnection> GetConnectionAsync()
    {
        return _connection;
    }

    /// <inheritdoc/>
    public async Task ExecuteInTransactionAsync(Func<IDbTransaction, Task> operations)
    {
        // SQLite: Use existing connection with transaction
        using var transaction = _connection.BeginTransaction(_config.TransactionIsolationLevel);

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

    public async Task CreateDedicatedTempTableAsync(string tempTableName, string sourceTableName)
    {
        // Get source table schema using PRAGMA
        var schema = await _connection.QueryAsync<dynamic>($"PRAGMA table_info({sourceTableName})");
        
        var columnDefs = schema.Select(col =>
        {
            var name = (string)col.name;
            var type = (string)col.type;
            var notNull = (long)col.notnull == 1 ? " NOT NULL" : "";
            var pk = (long)col.pk == 1 ? " PRIMARY KEY" : "";
            var dflt = col.dflt_value != null ? $" DEFAULT {col.dflt_value}" : "";
            
            return $"{name} {type}{notNull}{pk}{dflt}";
        });
        
        var createSql = $"CREATE TABLE IF NOT EXISTS {tempTableName} ({string.Join(", ", columnDefs)})";
        await _connection.ExecuteAsync(createSql);
    }

    public async Task DropTempTableAsync(string tempTableName)
    {
        await _connection.ExecuteAsync($"DROP TABLE IF EXISTS {tempTableName}");
    }

    public async Task<bool> TempTableExistsAsync(string tempTableName)
    {
        var count = await _connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = @TableName",
            new { TableName = tempTableName });
        
        return count > 0;
    }

    public async Task<IEnumerable<T>> GetRecordsFromSessionsAsync<T>(IEnumerable<Guid> sessionIds, Guid? tenantId = null) where T : ISyncEntity
    {
        var sessionIdList = sessionIds.ToList();
        if (!sessionIdList.Any())
            return Enumerable.Empty<T>();

        var tableName = TableNameResolver.GetTableName<T>();
        var sessionIdStrings = sessionIdList.Select(id => id.ToString()).ToList();
        var columns = EntityReflectionHelper.GetColumnsForServerSelect<T>();
        var columnList = string.Join(", ", columns);
        
        var sql = $@"
            SELECT {columnList}, SyncSessionId
            FROM {tableName}
            WHERE SyncSessionId IN @SessionIds";
        
        if (typeof(IMultiTenantSyncEntity).IsAssignableFrom(typeof(T)) && tenantId.HasValue)
        {
            sql += " AND TenantId = @TenantId";
            return await _connection.QueryAsync<T>(sql, new { SessionIds = sessionIdStrings, TenantId = tenantId });
        }
        
        return await _connection.QueryAsync<T>(sql, new { SessionIds = sessionIdStrings });
    }

    public async Task CreateSessionAsync(SessionRecord session, IDbTransaction? transaction = null)
    {
        await _connection.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, TenantId, DeviceId, UserId, UserDisplayName,
                                      SessionType, Status, CreatedAtUtc, LastActivityUtc,
                                      CommittedAtUtc, ErrorMessage, TotalRows, RowCountsJson)
            VALUES (@SessionId, @TenantId, @DeviceId, @UserId, @UserDisplayName,
                    @SessionType, @Status, @CreatedAtUtc, @LastActivityUtc,
                    @CommittedAtUtc, @ErrorMessage, @TotalRows, @RowCountsJson)",
            new
            {
                SessionId = session.SessionId.ToString(),
                TenantId = session.TenantId?.ToString(),
                DeviceId = session.DeviceId?.ToString(),
                session.UserId,
                session.UserDisplayName,
                session.SessionType,
                session.Status,
                CreatedAtUtc = session.CreatedAtUtc.ToString("O"),
                LastActivityUtc = session.LastActivityUtc.ToString("O"),
                CommittedAtUtc = session.CommittedAtUtc?.ToString("O"),
                session.ErrorMessage,
                session.TotalRows,
                session.RowCountsJson,
            }, transaction);
    }

    public async Task<IEnumerable<Guid>> FindUnseenSessionIdsAsync(Guid deviceId)
    {
        var sessionIds = await _connection.QueryAsync<string>(@"
            SELECT SessionId 
            FROM SessionRecords
            WHERE SessionId NOT IN (
                SELECT SessionId 
                FROM ClientProcessedSessions
                WHERE DeviceId = @DeviceId
            )
            ORDER BY CreatedAtUtc",
            new { DeviceId = deviceId.ToString() });

        return sessionIds.Select(id => Guid.Parse(id));
    }

    public async Task MarkSessionsProcessedAsync(Guid deviceId, IEnumerable<Guid> sessionIds, IDbTransaction? transaction = null)
    {
        foreach (var sessionId in sessionIds)
        {
            await _connection.ExecuteAsync(@"
                INSERT OR IGNORE INTO ClientProcessedSessions (DeviceId, SessionId, ProcessedAtUtc)
                VALUES (@DeviceId, @SessionId, @ProcessedAtUtc)",
                new
                {
                    DeviceId = deviceId.ToString(),
                    SessionId = sessionId.ToString(),
                    ProcessedAtUtc = DateTime.UtcNow.ToString("O")
                },
                transaction);
        }
    }

    /// <inheritdoc/>
    public async Task AcknowledgeSeedAsync(Guid deviceId, Guid tenantId)
    {
        var sql = @"
            INSERT OR IGNORE INTO ClientProcessedSessions (DeviceId, SessionId, ProcessedAtUtc)
            SELECT @DeviceId, SessionId, @ProcessedAtUtc
            FROM SessionRecords
            WHERE Status = 'Committed'
              AND (TenantId = @TenantId OR TenantId IS NULL)";

        await _connection.ExecuteAsync(sql, new
        {
            DeviceId = deviceId.ToString(),
            TenantId = tenantId.ToString(),
            ProcessedAtUtc = DateTime.UtcNow.ToString("O")
        });
    }
    
    #region Cleanup Operations
    
    /// <inheritdoc/>
    public async Task<List<SessionRecord>> FindStaleSessionsAsync(DateTime cutoffTime)
    {
        var results = await _connection.QueryAsync(@"
            SELECT SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc
            FROM SessionRecords
            WHERE Status IN ('Staging', 'Ready', 'Processing', 'Pulling')
              AND LastActivityUtc < @CutoffTime
            ORDER BY LastActivityUtc",
            new { CutoffTime = cutoffTime.ToString("O") });
        
        return results.Select(row => new SessionRecord
        {
            SessionId = Guid.Parse((string)row.SessionId),
            SessionType = (string)row.SessionType,
            Status = (string)row.Status,
            CreatedAtUtc = DateTime.Parse((string)row.CreatedAtUtc),
            LastActivityUtc = DateTime.Parse((string)row.LastActivityUtc)
        }).ToList();
    }
    
    /// <inheritdoc/>
    public async Task MarkSessionFailedAsync(Guid sessionId, string errorMessage)
    {
        await _connection.ExecuteAsync(@"
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
        var tables = await _connection.QueryAsync<dynamic>(@"
            SELECT TempTableName, UsesSharedTable
            FROM SyncSessionTables
            WHERE SessionId = @SessionId
              AND TempTableName IS NOT NULL",
            new { SessionId = sessionId.ToString() });
        
        return tables.Select(t => new TempTableInfo
        {
            TempTableName = (string)t.TempTableName,
            UsesSharedTable = (int)t.UsesSharedTable == 1
        }).ToList();
    }
    
    /// <inheritdoc/>
    public async Task<int> DeleteFromSharedTempTableAsync(string tableName, Guid sessionId)
    {
        return await _connection.ExecuteAsync(
            $"DELETE FROM {tableName} WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
    }
    
    /// <inheritdoc/>
    public async Task DeleteSessionTablesAsync(Guid sessionId)
    {
        await _connection.ExecuteAsync(@"
            DELETE FROM SyncSessionTables
            WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
    }
    
    /// <inheritdoc/>
    public async Task<List<Guid>> FindOldSessionsAsync(DateTime cutoffDate, string[] statuses)
    {
        var sessions = await _connection.QueryAsync<string>(@"
            SELECT SessionId
            FROM SessionRecords
            WHERE Status IN @Statuses
              AND CreatedAtUtc < @CutoffDate",
            new
            {
                Statuses = statuses,
                CutoffDate = cutoffDate.ToString("O")
            });
        
        return sessions.Select(s => Guid.Parse(s)).ToList();
    }
    
    /// <inheritdoc/>
    public async Task DeleteClientProcessedSessionsAsync(IEnumerable<Guid> sessionIds)
    {
        var sessionIdStrings = sessionIds.Select(s => s.ToString()).ToList();
        await _connection.ExecuteAsync(@"
            DELETE FROM ClientProcessedSessions
            WHERE SessionId IN @SessionIds",
            new { SessionIds = sessionIdStrings });
    }
    
    /// <inheritdoc/>
    public async Task DeleteSessionsAsync(IEnumerable<Guid> sessionIds)
    {
        var sessionIdStrings = sessionIds.Select(s => s.ToString()).ToList();
        
        // First delete session tables
        await _connection.ExecuteAsync(@"
            DELETE FROM SyncSessionTables
            WHERE SessionId IN @SessionIds",
            new { SessionIds = sessionIdStrings });
        
        // Then delete sessions
        await _connection.ExecuteAsync(@"
            DELETE FROM SessionRecords
            WHERE SessionId IN @SessionIds",
            new { SessionIds = sessionIdStrings });
    }
    
    /// <inheritdoc/>
    public async Task<int> DeleteOldSharedTempRowsAsync(string tableName, DateTime cutoffTime)
    {
        // Check if table exists first
        var tableExists = await _connection.ExecuteScalarAsync<int>($@"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = '{tableName}'") > 0;
        
        if (!tableExists)
            return 0;
        
        return await _connection.ExecuteAsync(
            $@"DELETE FROM {tableName} WHERE {TempTableSessionIdColumn} IN (
               SELECT SessionId FROM SessionRecords WHERE CreatedAtUtc < @CutoffTime)",
            new { CutoffTime = cutoffTime.ToString("O") });
    }
    
    /// <inheritdoc/>
    public async Task<List<string>> FindDedicatedTempTablesAsync()
    {
        // Find all tables matching dedicated temp table pattern
        var tables = await _connection.QueryAsync<string>(@"
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND (name LIKE 'TempPush_%' OR name LIKE 'TempPull_%')
              AND name NOT IN (
                  'TempPushCustomers',
                  'TempPushOrders',
                  'TempPushOrderItems',
                  'TempPullCustomers',
                  'TempPullOrders',
                  'TempPullOrderItems'
              )
            ORDER BY name");
        
        return tables.ToList();
    }
    
    /// <inheritdoc/>
    public async Task<List<string>> FindActiveTempTableNamesAsync()
    {
        var activeTables = await _connection.QueryAsync<string>(@"
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
        // Check if table exists first
        var tableExists = await _connection.ExecuteScalarAsync<int>($@"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = '{tableName}'") > 0;
        
        if (!tableExists)
            return 0;
        
        return await _connection.ExecuteScalarAsync<int>(
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
        await _connection.ExecuteAsync(@"
            INSERT INTO SyncSessionTables 
            (SessionId, TableName, TempTableName, ProcessingPriority, UsesSharedTable, EstimatedRecordCount, Status)
            VALUES (@SessionId, @TableName, @TempTableName, @Priority, @UsesSharedTable, @EstimatedRecordCount, 'Staging')",
            new
            {
                SessionId = sessionId.ToString(),
                TableName = tableName,
                TempTableName = tempTableName,
                Priority = priority,
                UsesSharedTable = usesSharedTable ? 1 : 0,
                EstimatedRecordCount = estimatedRecordCount
            });
    }
    
    /// <inheritdoc/>
    public async Task<SessionRecord?> GetSessionAsync(Guid sessionId)
    {
        var sessions = await _connection.QueryAsync<dynamic>(@"
            SELECT SessionId, SessionType, Status, SyncVersion,
                   CreatedAtUtc, LastActivityUtc, CommittedAtUtc, ErrorMessage
            FROM SessionRecords
            WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
        
        var session = sessions.FirstOrDefault();
        if (session == null)
            return null;
        
        return new SessionRecord
        {
            SessionId = Guid.Parse((string)session.SessionId),
            SessionType = (string)session.SessionType,
            Status = (string)session.Status,
            SyncVersion = session.SyncVersion == null ? null : (long?)session.SyncVersion,
            CreatedAtUtc = DateTime.Parse((string)session.CreatedAtUtc),
            LastActivityUtc = DateTime.Parse((string)session.LastActivityUtc),
            CommittedAtUtc = session.CommittedAtUtc == null ? null : DateTime.Parse((string)session.CommittedAtUtc),
            ErrorMessage = session.ErrorMessage
        };
    }
    
    /// <inheritdoc/>
    public async Task UpdateSessionActivityAsync(Guid sessionId)
    {
        await _connection.ExecuteAsync(@"
            UPDATE SessionRecords
            SET LastActivityUtc = datetime('now')
            WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
    }
    
    /// <inheritdoc/>
    public async Task<bool> MarkSessionReadyAsync(Guid sessionId)
    {
        var rowsAffected = await _connection.ExecuteAsync(@"
            UPDATE SessionRecords
            SET Status = 'Ready',
                LastActivityUtc = datetime('now')
            WHERE SessionId = @SessionId
              AND Status = 'Staging'",
            new { SessionId = sessionId.ToString() });
        
        return rowsAffected > 0;
    }
    
    /// <inheritdoc/>
    public async Task<bool> SessionExistsAsync(Guid sessionId, string? expectedStatus = null)
    {
        var sql = expectedStatus == null
            ? "SELECT COUNT(*) FROM SessionRecords WHERE SessionId = @SessionId"
            : "SELECT COUNT(*) FROM SessionRecords WHERE SessionId = @SessionId AND Status = @Status";
        
        var count = await _connection.ExecuteScalarAsync<int>(sql, new
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
        var result = await _connection.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT TempTableName, UsesSharedTable
            FROM SyncSessionTables
            WHERE SessionId = @SessionId AND TableName = @TableName",
            new { SessionId = sessionId.ToString(), TableName = tableName });
        
        if (result == null)
            return null;
        
        return new TempTableInfo
        {
            TempTableName = (string)result.TempTableName,
            UsesSharedTable = (int)result.UsesSharedTable == 1
        };
    }
    
    /// <inheritdoc/>
    public async Task<int> CountTempTableRecordsAsync(
        string tempTableName,
        Guid? sessionId,
        bool usesSharedTable)
    {
        var countSql = usesSharedTable
            ? $"SELECT COUNT(*) FROM {tempTableName} WHERE SessionId = @SessionId"
            : $"SELECT COUNT(*) FROM {tempTableName}";
        
        return await _connection.ExecuteScalarAsync<int>(
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
        await _connection.ExecuteAsync(@"
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
            var columnList = string.Join(", ", new[] { TempTableSessionIdColumn }.Concat(columns));
            var valueList = string.Join(", ", new[] { $"@{TempTableSessionIdColumn}" }.Concat(columns.Select(c => $"@{c}")));
            insertSql = $"INSERT INTO {tempTableName} ({columnList}) VALUES ({valueList})";
        }
        else
        {
            var columnList = string.Join(", ", columns);
            var valueList = string.Join(", ", columns.Select(c => $"@{c}"));
            insertSql = $"INSERT INTO {tempTableName} ({columnList}) VALUES ({valueList})";
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
                // Convert DateTime to ISO 8601 string for SQLite
                var value = kvp.Value is DateTime dt ? dt.ToString("O") : kvp.Value;
                p.Add(kvp.Key, value);
            }
            return p;
        }).ToArray();

        await _connection.ExecuteAsync(insertSql, parameters);

        return convertedRecords.Count;
    }
    
    
    /// <inheritdoc/>
    public async Task<PullBatchResult> GetPullBatchAsync(
        string tempTableName,
        Guid pullSessionId,
        int offset,
        int limit)
    {
        // Get total count
        var countSql = $"SELECT COUNT(*) FROM {tempTableName} WHERE {TempTableSessionIdColumn} = @PullSessionId";
        var totalRecords = await _connection.ExecuteScalarAsync<int>(
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
        var columnList = string.Join(", ", columns);

        // Explicitly select only business columns (exclude metadata)
        var batchSql = $@"
            SELECT {columnList}
            FROM {tempTableName}
            WHERE {TempTableSessionIdColumn} = @PullSessionId
            ORDER BY Id
            LIMIT @Limit OFFSET @Offset";

        var dynamicRecords = await _connection.QueryAsync(
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
        int totalDeleted = 0;
        
        foreach (var tableName in tableNames)
        {
            // Check if table exists first
            var tableExists = await _connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name = @TableName",
                new { TableName = tableName }) > 0;
            
            if (!tableExists)
                continue;
            
            var sql = $"DELETE FROM {tableName} WHERE {TempTableSessionIdColumn} = @PullSessionId";
            var deleted = await _connection.ExecuteAsync(sql, new { PullSessionId = pullSessionId.ToString() });
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

        var sessions = await _connection.QueryAsync<SessionRecord>(sql, new { Limit = limit });
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

        var tables = await _connection.QueryAsync<SessionTableInfo>(
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
        // Get column list for dynamic query building
        var columns = _metadataCache.GetColumnsForServerUpsert(tableName);
        var insertColumnList = string.Join(", ", columns);
        // SELECT: preserve client ModifiedAtUtc when provided; fall back to server time if null.
        var selectColumnList = string.Join(", ", columns.Select(c =>
            c == "ModifiedAtUtc"
                ? "COALESCE(ModifiedAtUtc, datetime('now'))"
                : c));
        var updateList = string.Join(", ", columns.Where(c => c != "Id").Select(c => $"{c} = excluded.{c}"));

        // Build upsert SQL
        // ModifiedAtUtc: client value preserved when set; falls back to datetime('now') if null.
        // SessionRecord.CommittedAtUtc is the authoritative server-side processing timestamp.
        // NOTE: SyncVersion is NOT on records - only on SessionRecords table
        var sql = $@"
            INSERT INTO {tableName} ({insertColumnList}, ModifiedByUserId, IsDeleted, SyncSessionId)
            SELECT {selectColumnList}, ModifiedByUserId, IsDeleted, @SessionId
            FROM {tempTableName}
            {(usesSharedTable ? "WHERE SessionId = @SessionId" : "")}
            ON CONFLICT(Id) DO UPDATE SET
                {updateList},
                ModifiedByUserId = excluded.ModifiedByUserId,
                IsDeleted = excluded.IsDeleted,
                SyncSessionId = excluded.SyncSessionId";

        var rowsAffected = await transaction.Connection!.ExecuteAsync(
            sql,
            new { SessionId = sessionId.ToString() },
            transaction);
            
        return rowsAffected;
    }
    
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
                CommittedAtUtc = CASE WHEN @Status IN ('Committed', 'Completed') THEN datetime('now') ELSE CommittedAtUtc END,
                ErrorMessage = COALESCE(@ErrorMessage, ErrorMessage),
                TotalRows = COALESCE(@TotalRows, TotalRows),
                RowCountsJson = COALESCE(@RowCountsJson, RowCountsJson),
                LastActivityUtc = datetime('now')
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
            await _connection.ExecuteAsync(sql, parameters);
        }
    }
    
    public async Task<int> CountRecordsFromSessionsAsync(string tableName, IEnumerable<Guid> sessionIds, Guid? tenantId = null)
    {
        var sessionIdList = sessionIds.Select(id => id.ToString()).ToList();
        
        if (!sessionIdList.Any())
            return 0;
        
        var sql = $@"
            SELECT COUNT(*)
            FROM {tableName}
            WHERE SyncSessionId IN ({string.Join(",", sessionIdList.Select((_, i) => $"@SessionId{i}"))})";
        
        var parameters = new DynamicParameters();
        for (int i = 0; i < sessionIdList.Count; i++)
        {
            parameters.Add($"SessionId{i}", sessionIdList[i]);
        }
        
        // Multi-tenant filtering
        if (_metadataCache.IsMultiTenant(tableName) && tenantId.HasValue)
        {
            sql += " AND TenantId = @TenantId";
            parameters.Add("TenantId", tenantId.Value.ToString());
        }
        
        var count = await _connection.ExecuteScalarAsync<int>(sql, parameters);
        return count;
    }
    
    public async Task<int> SnapshotRecordsForPullAsync(
        string tempTableName,
        string sourceTableName,
        IEnumerable<Guid> sessionIds,
        Guid pullSessionId,
        bool usesSharedTable,
        Guid? tenantId = null)
    {
        var sessionIdList = sessionIds.Select(id => id.ToString()).ToList();
        
        if (!sessionIdList.Any())
            return 0;
        
        // Get all columns for server SELECT (business + sync columns)
        var allColumns = _metadataCache.GetColumnsForServerSelect(sourceTableName);
        var columnList = string.Join(", ", allColumns);
        
        // Build IN clause with numbered parameters
        var sessionInClause = string.Join(",", sessionIdList.Select((_, i) => $"@SessionId{i}"));
        
        // Multi-tenant filtering
        var tenantFilter = "";
        if (_metadataCache.IsMultiTenant(sourceTableName) && tenantId.HasValue)
        {
            tenantFilter = " AND TenantId = @TenantId";
        }
        
        // Build INSERT SELECT statement
        string sql;
        
        if (usesSharedTable)
        {
            sql = $@"
                INSERT INTO {tempTableName} 
                ({TempTableSessionIdColumn}, {columnList})
                SELECT 
                    @PullSessionId,
                    {columnList}
                FROM {sourceTableName}
                WHERE SyncSessionId IN ({sessionInClause}){tenantFilter}";
        }
        else
        {
            sql = $@"
                INSERT INTO {tempTableName} 
                ({columnList})
                SELECT 
                    {columnList}
                FROM {sourceTableName}
                WHERE SyncSessionId IN ({sessionInClause}){tenantFilter}";
        }
        
        var parameters = new DynamicParameters();
        parameters.Add("PullSessionId", pullSessionId.ToString());
        for (int i = 0; i < sessionIdList.Count; i++)
        {
            parameters.Add($"SessionId{i}", sessionIdList[i]);
        }
        if (_metadataCache.IsMultiTenant(sourceTableName) && tenantId.HasValue)
        {
            parameters.Add("TenantId", tenantId.Value.ToString());
        }
        
        var rowsInserted = await _connection.ExecuteAsync(sql, parameters);
        return rowsInserted;
    }

    public async Task<IReadOnlyList<SyncSessionSummary>> GetRecentSessionsAsync(Guid? tenantId, int limit = 50)
    {
        const string sql = @"
            SELECT s.SessionId, s.TenantId, s.DeviceId, s.SessionType, s.SyncVersion, s.CommittedAtUtc,
                   st.TableName, st.ActualRecordCount
            FROM SessionRecords s
            LEFT JOIN SyncSessionTables st ON st.SessionId = s.SessionId
            WHERE s.Status = 'Committed'
              AND (@TenantId IS NULL OR s.TenantId = @TenantId)
            ORDER BY s.SyncVersion DESC
            LIMIT @Limit";

        var rows = await _connection.QueryAsync<(Guid SessionId, Guid? TenantId, Guid? DeviceId, string SessionType,
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

        var results = await _connection.QueryAsync<SyncSessionTable>(sql, new { SessionId = sessionId.ToString() });
        return results.ToList().AsReadOnly();
    }

    public async Task<int> ExecuteRawSqlAsync(string sql)
    {
        return await _connection.ExecuteAsync(sql);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetTableColumnsAsync(string tableName)
    {
        // pragma_table_info() is a table-valued function available in SQLite 3.16+ (we require 3.30+).
        // Returns one row per column; the 'name' field is the column name.
        var columns = await _connection.QueryAsync<string>(
            "SELECT name FROM pragma_table_info(@TableName)",
            new { TableName = tableName });
        return columns.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// SQLite stub — shared temp tables are not used in SQLite server deployments.
    /// To be implemented if SQLite server-side sync requires shared temp table support.
    /// </remarks>
    public Task EnsureSharedTempTablesAsync() => Task.CompletedTask;
    
    /// <inheritdoc/>
    public Task<int> UpsertDirectAsync(string tableName, List<object> records, Guid sessionId, IDbTransaction? transaction = null)
    {
        // SQLite server database is used only for integration testing.
        // Direct write operations target MySQL in production.
        throw new NotSupportedException("UpsertDirectAsync is not supported by SqliteServerDatabase. Use MySqlServerDatabase for direct write operations.");
    }

    /// <inheritdoc/>
    public Task<int> SoftDeleteDirectAsync(
        string tableName,
        Guid id,
        string userId,
        Guid sessionId,
        Guid? tenantId,
        IDbTransaction? transaction = null)
    {
        throw new NotSupportedException("SoftDeleteDirectAsync is not supported by SqliteServerDatabase. Use MySqlServerDatabase for direct write operations.");
    }

    /// <inheritdoc />
    public Task<Dictionary<string, object?>?> GetByIdAsync(
        string tableName, Guid id, Guid? tenantId = null)
    {
        throw new NotSupportedException("GetByIdAsync is not supported by SqliteServerDatabase. Use MySqlServerDatabase for data query operations.");
    }

    /// <inheritdoc />
    public Task<DataQueryResult> QueryAsync(
        string tableName, DataQuery query, Guid? tenantId = null)
    {
        throw new NotSupportedException("QueryAsync is not supported by SqliteServerDatabase. Use MySqlServerDatabase for data query operations.");
    }
    
    #endregion

    #region Seed Snapshots

    /// <inheritdoc/>
    public async Task<SeedSnapshot?> FindSeedSnapshotAsync(Guid deviceId, Guid tenantId)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<dynamic>(
            @"SELECT SeedId, DeviceId, TenantId, Status, CreatedAtUtc, LastActivityUtc
              FROM SeedSnapshots
              WHERE DeviceId = @DeviceId AND TenantId = @TenantId",
            new { DeviceId = deviceId.ToString(), TenantId = tenantId.ToString() });

        if (row == null) return null;

        return new SeedSnapshot
        {
            SeedId     = Guid.Parse((string)row.SeedId),
            DeviceId   = Guid.Parse((string)row.DeviceId),
            TenantId   = Guid.Parse((string)row.TenantId),
            Status     = (string)row.Status,
            CreatedAtUtc     = DateTime.Parse((string)row.CreatedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
            LastActivityUtc  = DateTime.Parse((string)row.LastActivityUtc, null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    /// <inheritdoc/>
    public async Task InsertSeedSnapshotAsync(Guid seedId, Guid deviceId, Guid tenantId)
    {
        var now = DateTime.UtcNow.ToString("O");
        await _connection.ExecuteAsync(
            @"INSERT INTO SeedSnapshots (SeedId, DeviceId, TenantId, Status, CreatedAtUtc, LastActivityUtc)
              VALUES (@SeedId, @DeviceId, @TenantId, 'Active', @Now, @Now)",
            new { SeedId = seedId.ToString(), DeviceId = deviceId.ToString(), TenantId = tenantId.ToString(), Now = now });
    }

    /// <inheritdoc/>
    public async Task UpdateSeedSnapshotActivityAsync(Guid seedId)
    {
        await _connection.ExecuteAsync(
            "UPDATE SeedSnapshots SET LastActivityUtc = @Now WHERE SeedId = @SeedId",
            new { SeedId = seedId.ToString(), Now = DateTime.UtcNow.ToString("O") });
    }

    /// <inheritdoc/>
    public async Task UpdateSeedSnapshotStatusAsync(Guid seedId, string status)
    {
        await _connection.ExecuteAsync(
            "UPDATE SeedSnapshots SET Status = @Status, LastActivityUtc = @Now WHERE SeedId = @SeedId",
            new { SeedId = seedId.ToString(), Status = status, Now = DateTime.UtcNow.ToString("O") });
    }

    /// <inheritdoc/>
    public async Task DeleteSeedSnapshotAsync(Guid seedId)
    {
        await _connection.ExecuteAsync(
            "DELETE FROM SeedSnapshots WHERE SeedId = @SeedId",
            new { SeedId = seedId.ToString() });
    }

    /// <inheritdoc/>
    public async Task CreateSeedSnapshotTableAsync(string snapTableName, string sourceTableName, Guid? tenantId)
    {
        await _connection.ExecuteAsync($"DROP TABLE IF EXISTS \"{snapTableName}\"");
        if (tenantId.HasValue)
            await _connection.ExecuteAsync(
                $"CREATE TABLE \"{snapTableName}\" AS SELECT * FROM \"{sourceTableName}\" WHERE TenantId = @TenantId",
                new { TenantId = tenantId.Value.ToString() });
        else
            await _connection.ExecuteAsync(
                $"CREATE TABLE \"{snapTableName}\" AS SELECT * FROM \"{sourceTableName}\"");
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<SeedSnapshot>> FindOrphanedSeedSnapshotsAsync(DateTime cutoff)
    {
        var rows = await _connection.QueryAsync<dynamic>(
            @"SELECT SeedId, DeviceId, TenantId, Status, CreatedAtUtc, LastActivityUtc
              FROM SeedSnapshots
              WHERE Status = 'Active' AND LastActivityUtc < @Cutoff",
            new { Cutoff = cutoff.ToString("O") });

        return rows.Select(row => new SeedSnapshot
        {
            SeedId     = Guid.Parse((string)row.SeedId),
            DeviceId   = Guid.Parse((string)row.DeviceId),
            TenantId   = Guid.Parse((string)row.TenantId),
            Status     = (string)row.Status,
            CreatedAtUtc    = DateTime.Parse((string)row.CreatedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind),
            LastActivityUtc = DateTime.Parse((string)row.LastActivityUtc, null, System.Globalization.DateTimeStyles.RoundtripKind)
        }).ToList();
    }

    /// <inheritdoc/>
    public async Task<List<string>> FindSeedSnapshotTableNamesAsync(Guid seedId)
    {
        var suffix = seedId.ToString("N");
        var tables = await _connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE @Pattern",
            new { Pattern = $"SeedSnap_%_{suffix}" });
        return tables.AsList();
    }

    /// <inheritdoc/>
    public async Task<List<Dictionary<string, object?>>> GetSeedSnapshotBatchAsync(
        string snapTableName, int offset, int limit)
    {
        var rows = await _connection.QueryAsync(
            $"SELECT * FROM \"{snapTableName}\" ORDER BY Id LIMIT @Limit OFFSET @Offset",
            new { Limit = limit, Offset = offset });
        return rows
            .Select(r => ((IDictionary<string, object?>)r)
                .ToDictionary(kv => kv.Key, kv => kv.Value))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<List<Dictionary<string, object?>>> GetSeedSnapshotBatchAfterIdAsync(
        string snapTableName, string afterId, int limit)
    {
        var rows = await _connection.QueryAsync(
            $"SELECT * FROM \"{snapTableName}\" WHERE Id > @AfterId ORDER BY Id LIMIT @Limit",
            new { AfterId = afterId, Limit = limit });
        return rows
            .Select(r => ((IDictionary<string, object?>)r)
                .ToDictionary(kv => kv.Key, kv => kv.Value))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<int> GetSeedSnapshotCountAsync(string snapTableName)
    {
        return await _connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM \"{snapTableName}\"");
    }

    /// <inheritdoc/>
    public async Task DropSeedSnapshotTableAsync(string snapTableName)
    {
        await _connection.ExecuteAsync($"DROP TABLE IF EXISTS \"{snapTableName}\"");
    }

    #endregion
}
