using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SyncSession.Core.Models;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Service for direct writes to server data that remain visible to sync clients
/// via automatic session wrapping. Supports both batch (multiple tables, transactional)
/// and single-record operations.
/// </summary>
public interface IDirectWriteService
{
    /// <summary>
    /// Write multiple records across multiple tables in a single transaction.
    /// All records will share the same SyncSessionId and be visible to sync clients atomically.
    /// </summary>
    /// <param name="tableRecords">Dictionary of table name to list of entity objects</param>
    /// <param name="userId">User ID for ModifiedByUserId audit trail</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant validation</param>
    /// <returns>Result with session ID and records written per table</returns>
    /// <exception cref="ArgumentException">Invalid table name or entity validation failure</exception>
    /// <exception cref="InvalidOperationException">Tenant mismatch or business rule violation</exception>
    Task<DirectWriteBatchResult> WriteBatchAsync(
        Dictionary<string, List<object>> tableRecords,
        string userId,
        string? tenantId = null);

    /// <summary>
    /// Write or update a single record. Creates a sync session and commits immediately.
    /// </summary>
    /// <typeparam name="T">Entity type implementing ISyncEntity</typeparam>
    /// <param name="entity">Entity to write</param>
    /// <param name="userId">User ID for ModifiedByUserId audit trail</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant validation</param>
    /// <returns>Result with session ID and records written</returns>
    /// <exception cref="ArgumentException">Entity validation failure</exception>
    /// <exception cref="InvalidOperationException">Tenant mismatch or business rule violation</exception>
    Task<DirectWriteResult> WriteAsync<T>(T entity, string userId, string? tenantId = null)
        where T : ISyncEntity;

    /// <summary>
    /// Soft delete a record by ID (sets IsDeleted = true).
    /// </summary>
    /// <typeparam name="T">Entity type implementing ISyncEntity</typeparam>
    /// <param name="id">Entity ID to delete</param>
    /// <param name="userId">User ID for ModifiedByUserId audit trail</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant validation</param>
    /// <returns>Result with session ID and records written</returns>
    /// <exception cref="ArgumentException">Entity not found</exception>
    /// <exception cref="InvalidOperationException">Tenant mismatch or business rule violation</exception>
    Task<DirectWriteResult> DeleteAsync<T>(Guid id, string userId, string? tenantId = null)
        where T : ISyncEntity;

    /// <summary>
    /// Soft delete a record by table name and ID.
    /// Non-generic overload for use by controllers where the entity type is resolved at runtime.
    /// </summary>
    /// <param name="tableName">Registered sync table name</param>
    /// <param name="id">Entity ID to delete</param>
    /// <param name="userId">User ID for ModifiedByUserId audit trail</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant validation</param>
    /// <returns>Result with session ID and records written</returns>
    Task<DirectWriteResult> DeleteAsync(string tableName, Guid id, string userId, string? tenantId = null);
}
