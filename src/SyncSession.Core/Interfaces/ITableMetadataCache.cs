using System.Collections.Generic;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Provides cached metadata for sync tables, including column lists for different sync contexts.
/// Cache is built once at construction using reflection, then provides O(1) lookups.
/// </summary>
public interface ITableMetadataCache
{
    /// <summary>
    /// Gets columns for server SELECT during PULL operations (server → client).
    /// Includes: Business columns, ModifiedAtUtc, IsDeleted, ModifiedByUserId
    /// Excludes: IsDirty, SyncSessionId
    /// </summary>
    List<string> GetColumnsForServerSelect(string tableName);

    /// <summary>
    /// Gets columns for server UPSERT during PUSH operations (client → server).
    /// Includes: Business columns, IsDeleted, ModifiedByUserId
    /// Excludes: IsDirty (client-only), ModifiedAtUtc (server generates), SyncSessionId (server assigns)
    /// </summary>
    List<string> GetColumnsForServerUpsert(string tableName);

    /// <summary>
    /// Gets columns for direct upsert operations (Direct Write API).
    /// Same as ServerUpsert but includes SyncSessionId (set on entity by DirectWriteService).
    /// </summary>
    List<string> GetColumnsForDirectUpsert(string tableName);

    /// <summary>
    /// Gets columns for client UPSERT during PULL operations (server → client).
    /// Includes: Business columns, IsDirty, ModifiedAtUtc, IsDeleted, ModifiedByUserId
    /// Excludes: SyncSessionId (not needed for client storage)
    /// </summary>
    List<string> GetColumnsForPullUpsert(string tableName);

    /// <summary>
    /// Gets columns for client SELECT during PUSH operations (client → server).
    /// Includes: Business columns, IsDeleted, ModifiedByUserId
    /// Excludes: IsDirty, ModifiedAtUtc, SyncSessionId
    /// </summary>
    List<string> GetColumnsForPushSelect(string tableName);

    /// <summary>
    /// Gets the entity type for a table.
    /// </summary>
    Type GetEntityType(string tableName);

    /// <summary>
    /// Gets valid columns for incoming push data (API → temp table filtering).
    /// Used to intersect client-sent dictionary keys with columns the temp table accepts.
    /// Delegates to GetColumnsForServerUpsert (same column set, different intent).
    /// </summary>
    IReadOnlySet<string> GetValidPushColumns(string tableName);

    /// <summary>
    /// Whether the table's entity implements IMultiTenantSyncEntity.
    /// Pre-computed at construction (zero reflection at runtime).
    /// </summary>
    bool IsMultiTenant(string tableName);
}
