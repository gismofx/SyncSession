using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Interface for client-side database operations.
/// Table names are automatically extracted from <c>[SyncTable]</c> attributes on entity types.
/// </summary>
public interface IClientDatabase
{
    /// <summary>
    /// Gets a database connection for executing queries.
    /// </summary>
    /// <returns>An open database connection.</returns>
    Task<IDbConnection> GetConnectionAsync();
    
    /// <summary>
    /// Execute operations within a single transaction.
    /// Transaction commits on success, rolls back on exception.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<IDbTransaction, Task> action);
    
    // Version tracking (still uses string table name for flexibility)
    /// <summary>Gets the last successfully synced version for a table.</summary>
    /// <param name="tableName">Business table name (e.g., <c>Customers</c>).</param>
    /// <returns>Last synced version number, or 0 if never synced.</returns>
    Task<long> GetLastSyncVersionAsync(string tableName);

    /// <summary>Persists the last successfully synced version for a table.</summary>
    /// <param name="tableName">Business table name (e.g., <c>Customers</c>).</param>
    /// <param name="version">Version number to store.</param>
    Task UpdateLastSyncVersionAsync(string tableName, long version);
    
    // Generic type-safe operations (table name from [SyncTable] attribute)
    
    /// <summary>
    /// Get all dirty (modified) records from a table.
    /// Table name extracted from [SyncTable] attribute.
    /// Filters by <paramref name="tenantId"/> if entity implements IMultiTenantSyncEntity.
    /// </summary>
    Task<IEnumerable<T>> GetDirtyRecordsAsync<T>(Guid? tenantId = null) where T : ISyncEntity;
    
    /// <summary>
    /// Mark all dirty records as clean for a table.
    /// Table name extracted from [SyncTable] attribute.
    /// Filters by <paramref name="tenantId"/> if entity implements IMultiTenantSyncEntity.
    /// </summary>
    Task MarkRecordsCleanAsync<T>(Guid? tenantId = null) where T : ISyncEntity;
    
    /// <summary>
    /// Upsert multiple records from the server (batched).
    /// Table name extracted from [SyncTable] attribute.
    /// Validates TenantId matches <paramref name="tenantId"/> if entity implements IMultiTenantSyncEntity.
    /// </summary>
    Task UpsertBatchAsync<T>(IEnumerable<T> records, Guid? tenantId = null, IDbTransaction? transaction = null) where T : ISyncEntity;
}
