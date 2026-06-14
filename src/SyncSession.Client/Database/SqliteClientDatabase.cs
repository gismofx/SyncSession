using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;

namespace SyncSession.Client.Database;

/// <summary>
/// SQLite implementation of <see cref="IClientDatabase"/> with automatic tenant filtering.
/// </summary>
public class SqliteClientDatabase : IClientDatabase, IDisposable
{
    private readonly SqliteConnection _connection;

    static SqliteClientDatabase()
    {
        SqlMapper.AddTypeHandler(new SqliteGuidTypeHandler());
        SqlMapper.AddTypeHandler(new SqliteNullableGuidTypeHandler());
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SqliteClientDatabase"/> with an open SQLite connection.
    /// </summary>
    /// <param name="connection">Open SQLite connection to use for all database operations.</param>
    public SqliteClientDatabase(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc/>
    public async Task<IDbConnection> GetConnectionAsync()
    {
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync();

        return _connection;
    }

    /// <inheritdoc/>
    public async Task ExecuteInTransactionAsync(Func<IDbTransaction, Task> action)
    {
        var connection = await GetConnectionAsync();
        using var transaction = connection.BeginTransaction();
        
        try
        {
            await action(transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<long> GetLastSyncVersionAsync(string tableName)
    {
        var sql = "SELECT LastSyncVersion FROM LocalSyncState WHERE TableName = @TableName";
        var connection = await GetConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(sql, new { TableName = tableName });
    }

    /// <inheritdoc/>
    public async Task UpdateLastSyncVersionAsync(string tableName, long version)
    {
        var sql = @"
            INSERT INTO LocalSyncState (TableName, LastSyncVersion, LastSyncCompletedAtUtc)
            VALUES (@TableName, @Version, @Now)
            ON CONFLICT(TableName) DO UPDATE SET
                LastSyncVersion = @Version,
                LastSyncCompletedAtUtc = @Now";
        
        await _connection.ExecuteAsync(sql, new 
        { 
            TableName = tableName, 
            Version = version,
            Now = DateTime.UtcNow.ToString("O")
        });
    }

    /// <inheritdoc/>
    public async Task<string?> GetClientMetadataAsync(string key)
    {
        var sql = "SELECT Value FROM LocalSyncMetadata WHERE Key = @Key";
        var connection = await GetConnectionAsync();
        return await connection.ExecuteScalarAsync<string?>(sql, new { Key = key });
    }

    /// <inheritdoc/>
    public async Task SetClientMetadataAsync(string key, string value)
    {
        var sql = @"
            INSERT INTO LocalSyncMetadata (Key, Value, UpdatedAtUtc)
            VALUES (@Key, @Value, @Now)
            ON CONFLICT(Key) DO UPDATE SET
                Value = @Value,
                UpdatedAtUtc = @Now";

        var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(sql, new
        {
            Key = key,
            Value = value,
            Now = DateTime.UtcNow.ToString("O")
        });
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<T>> GetDirtyRecordsAsync<T>(Guid? tenantId = null) where T : ISyncEntity
    {
        var tableName = TableNameResolver.GetTableName<T>();
        var columns = EntityReflectionHelper.GetColumnsForClientSelect<T>();
        var columnList = string.Join(", ", columns);

        var sql = $"SELECT {columnList} FROM {tableName} WHERE IsDirty = 1";

        var connection = await GetConnectionAsync();

        if (typeof(IMultiTenantSyncEntity).IsAssignableFrom(typeof(T)) && tenantId != null)
        {
            sql += " AND TenantId = @TenantId";
            return await connection.QueryAsync<T>(sql, new { TenantId = tenantId });
        }

        return await connection.QueryAsync<T>(sql);
    }

    /// <inheritdoc/>
    public async Task MarkRecordsCleanAsync<T>(Guid? tenantId = null) where T : ISyncEntity
    {
        var tableName = TableNameResolver.GetTableName<T>();
        var sql = $"UPDATE {tableName} SET IsDirty = 0 WHERE IsDirty = 1";
        
        // Automatic tenant filtering for multi-tenant entities
        if (typeof(IMultiTenantSyncEntity).IsAssignableFrom(typeof(T)) && tenantId != null)
        {
            sql += " AND TenantId = @TenantId";
            await _connection.ExecuteAsync(sql, new { TenantId = tenantId });
        }
        else
        {
            await _connection.ExecuteAsync(sql);
        }
    }

    /// <inheritdoc/>
    public async Task UpsertBatchAsync<T>(IEnumerable<T> records, Guid? tenantId = null, IDbTransaction? transaction = null) where T : ISyncEntity
    {
        var recordList = records.ToList();
        
        if (!recordList.Any())
            return;

        var tableName = TableNameResolver.GetTableName<T>();

        // Tenant validation
        if (typeof(IMultiTenantSyncEntity).IsAssignableFrom(typeof(T)) && tenantId != null)
        {
            var multiTenantRecords = recordList.Cast<IMultiTenantSyncEntity>();
            if (multiTenantRecords.Any(r => r.TenantId != tenantId))
            {
                throw new System.Security.SecurityException(
                    $"Tenant validation failed. Attempted to upsert records with TenantId mismatch. Expected: {tenantId}");
            }
        }

        var columns = EntityReflectionHelper.GetColumnsForPullUpsert<T>();
        var columnList = string.Join(", ", columns);
        var paramList = string.Join(", ", columns.Select(c => $"@{c}"));
        var updateList = string.Join(", ", columns.Where(c => c != "Id").Select(c => $"{c} = excluded.{c}"));
        
        var sql = $@"
            INSERT INTO {tableName} ({columnList})
            VALUES ({paramList})
            ON CONFLICT(Id) DO UPDATE SET
                {updateList}";

        try
        {
            if (transaction != null)
            {
                await transaction.Connection!.ExecuteAsync(sql, recordList, transaction);
            }
            else
            {
                await _connection.ExecuteAsync(sql, recordList);
            }
        }
        catch (SqliteException ex)
        {
            var errorType = ClassifySqliteError(ex.SqliteErrorCode);
            
            // Diagnostic logging for development/debugging
            Debug.WriteLine($"[SqliteClientDatabase] Batch upsert failed for {tableName}. " +
                          $"Records: {recordList.Count}, Error Type: {errorType}, " +
                          $"SQLite Error Code: {(int)ex.SqliteErrorCode}, Message: {ex.Message}");
            
            throw;
        }
    }

    /// <summary>
    /// Initializes the local sync state and metadata tables if they do not already exist.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS LocalSyncState (
                TableName TEXT PRIMARY KEY,
                LastSyncVersion INTEGER NOT NULL DEFAULT 0,
                LastSyncCompletedAtUtc TEXT,
                CreatedAtUtc TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAtUtc TEXT NOT NULL DEFAULT (datetime('now'))
            )");

        await _connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS LocalSyncMetadata (
                Key          TEXT NOT NULL PRIMARY KEY,
                Value        TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL DEFAULT (datetime('now'))
            )");
    }

    /// <summary>
    /// Classifies a SQLite error code into an actionable category string.
    /// </summary>
    private static string ClassifySqliteError(int errorCode)
    {
        return errorCode switch
        {
            19 => "CONSTRAINT_VIOLATION",    // PRIMARY KEY, UNIQUE, FOREIGN KEY, NOT NULL, CHECK
            1 => "SQL_ERROR",                // SQL error or missing database
            5 => "DATABASE_LOCKED",          // Database is locked (retry)
            6 => "TABLE_LOCKED",             // A table in the database is locked (retry)
            10 => "IO_ERROR",                // Disk I/O error
            13 => "DATABASE_FULL",           // Database or disk is full
            14 => "CANT_OPEN",               // Unable to open database file
            18 => "DATA_TOO_BIG",            // String or BLOB exceeds size limit
            20 => "TYPE_MISMATCH",           // Data type mismatch
            _ => "UNKNOWN_ERROR"
        };
    }

    private bool _disposed;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        if (_connection?.State == ConnectionState.Open)
            _connection.Close();

        _connection?.Dispose();
        _disposed = true;
    }
}
