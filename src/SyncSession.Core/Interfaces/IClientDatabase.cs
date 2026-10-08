using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using SyncSession.Core.Models;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Interface for client-side database operations.
/// Table names are automatically extracted from <c>[SyncTable]</c> attributes on entity types.
/// </summary>
public interface IClientDatabase
{
    /// <summary>
    /// Creates the library's client-side bookkeeping table (<c>LocalSyncMetadata</c>)
    /// if it does not already exist.
    /// </summary>
    /// <remarks>
    /// Call this <b>once at application startup</b>, before any seeding or synchronization runs —
    /// the sync engine does not provision this table itself. The operation is idempotent
    /// (<c>CREATE TABLE IF NOT EXISTS</c>), so it is safe to call on every startup.
    /// SQLite-backed implementations can use <c>SqliteClientSchema.AllStatements</c> for the DDL.
    /// </remarks>
    Task InitializeAsync();

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
    
    // Client metadata key/value store (schema-version-independent)
    /// <summary>
    /// Gets a value from the client metadata store, or <c>null</c> if the key is not present.
    /// Keys are case-sensitive.
    /// </summary>
    /// <param name="key">Metadata key (see <c>ClientMetadataKeys</c>).</param>
    Task<string?> GetClientMetadataAsync(string key);

    /// <summary>
    /// Stores (inserts or overwrites) a value in the client metadata store. Keys are case-sensitive.
    /// </summary>
    /// <param name="key">Metadata key (see <c>ClientMetadataKeys</c>).</param>
    /// <param name="value">Value to persist.</param>
    Task SetClientMetadataAsync(string key, string value);
    
    // Generic type-safe operations (table name from [SyncTable] attribute)
    
    /// <summary>
    /// Get all dirty (modified) records from a table.
    /// Table name extracted from [SyncTable] attribute.
    /// Filters by <paramref name="tenantId"/> if entity implements IMultiTenantSyncEntity.
    /// </summary>
    Task<IEnumerable<T>> GetDirtyRecordsAsync<T>(Guid? tenantId = null) where T : ISyncEntity;
    
    /// <summary>
    /// Mark the given pushed records clean, and only those that have not been saved again since the
    /// push read them. Called by the sync engine only after the server has committed the push.
    /// Table name extracted from [SyncTable] attribute.
    /// Filters by <paramref name="tenantId"/> if entity implements IMultiTenantSyncEntity.
    /// </summary>
    /// <remarks>
    /// Implementations must clear <c>IsDirty</c> on a row only when its <c>Id</c> matches a stamp
    /// <b>and</b> its current <c>ModifiedAtUtc</c> still equals that stamp's value (both null counts
    /// as equal). Never clear rows by "IsDirty = 1" alone: a row saved while the push was in flight
    /// was not sent, and clearing it means it is never sent at all.
    /// </remarks>
    /// <param name="pushed">The records the push sent, as they were when it read them.</param>
    /// <param name="tenantId">Tenant filter for multi-tenant entities.</param>
    Task MarkRecordsCleanAsync<T>(IReadOnlyCollection<PushedRecordStamp> pushed, Guid? tenantId = null) where T : ISyncEntity;
    
    /// <summary>
    /// Upsert multiple records from the server (batched).
    /// Table name extracted from [SyncTable] attribute.
    /// Validates TenantId matches <paramref name="tenantId"/> if entity implements IMultiTenantSyncEntity.
    /// </summary>
    Task UpsertBatchAsync<T>(IEnumerable<T> records, Guid? tenantId = null, IDbTransaction? transaction = null) where T : ISyncEntity;
}
