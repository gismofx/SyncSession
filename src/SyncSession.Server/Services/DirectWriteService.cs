using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Constants;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;

namespace SyncSession.Server.Services;

/// <summary>
/// Service for direct writes to server data that remain visible to sync clients.
/// Creates auto-committed sync sessions (no background queue) for immediate visibility.
/// </summary>
public class DirectWriteService : IDirectWriteService
{
    private readonly IServerDatabase _serverDb;
    private readonly ITableMetadataCache _metadataCache;
    private readonly IDirectWriteTenantValidator _tenantValidator;
    private readonly ILogger<DirectWriteService> _logger;

    public DirectWriteService(
        IServerDatabase serverDb,
        ITableMetadataCache metadataCache,
        IDirectWriteTenantValidator tenantValidator,
        ILogger<DirectWriteService> logger)
    {
        _serverDb = serverDb;
        _metadataCache = metadataCache;
        _tenantValidator = tenantValidator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DirectWriteBatchResult> WriteBatchAsync(
        Dictionary<string, List<object>> tableRecords,
        string userId,
        string? tenantId = null)
    {
        if (tableRecords == null || tableRecords.Count == 0)
            throw new ArgumentException("Batch must contain at least one table", nameof(tableRecords));

        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID is required", nameof(userId));

        _logger.LogInformation(
            "DirectWrite: Beginning batch write for user {UserId}, {TableCount} tables, {TotalRecords} total records",
            userId,
            tableRecords.Count,
            tableRecords.Values.Sum(records => records.Count));

        // Validate all table names exist in metadata cache
        foreach (var tableName in tableRecords.Keys)
        {
            try
            {
                _ = _metadataCache.GetEntityType(tableName);
            }
            catch (InvalidOperationException)
            {
                throw new ArgumentException($"Unknown table name: {tableName}", nameof(tableRecords));
            }
        }

        // Validate tenant authorization for multi-tenant entities
        await ValidateTenantAuthorizationAsync(tableRecords, userId, tenantId);

        // Create session and write all records in single transaction
        var sessionId = Guid.NewGuid();

        var committedAt = DateTime.UtcNow;
        var tableResults = new Dictionary<string, TableWriteResult>();

        await _serverDb.ExecuteInTransactionAsync(async (transaction) =>
        {
            // Create session
            var session = new SessionRecord
            {
                SessionId = sessionId,
                Status = SyncConstants.STATUS_STAGING,
                SessionType = SyncConstants.SESSION_TYPE_DIRECT,
                CreatedAtUtc = committedAt,
                LastActivityUtc = committedAt
            };
            await _serverDb.CreateSessionAsync(session, transaction);

            // Write records for each table
            foreach (var (tableName, records) in tableRecords)
            {
                var recordsWritten = await WriteRecordsToTableAsync(
                    tableName,
                    records,
                    sessionId,
                    userId,
                    tenantId,
                    transaction);

                tableResults[tableName] = new TableWriteResult { RecordsWritten = recordsWritten };
            }

            // Commit session immediately (no background queue)
            await _serverDb.UpdateSessionStatusAsync(sessionId, SyncConstants.STATUS_COMMITTED, transaction, syncVersion: null);
        });

        _logger.LogInformation(
            "DirectWrite: Batch write completed. SessionId={SessionId}, TablesWritten={TableCount}, TotalRecords={TotalRecords}",
            sessionId,
            tableResults.Count,
            tableResults.Values.Sum(t => t.RecordsWritten));

        return new DirectWriteBatchResult
        {
            SessionId = sessionId,
            Tables = tableResults,
            CommittedAtUtc = committedAt
        };
    }

    /// <inheritdoc />
    public async Task<DirectWriteResult> WriteAsync<T>(T entity, string userId, string? tenantId = null)
        where T : ISyncEntity
    {
        if (entity == null)
            throw new ArgumentException("Entity cannot be null", nameof(entity));

        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID is required", nameof(userId));

        var tableName = TableNameResolver.GetTableName<T>();

        _logger.LogInformation(
            "DirectWrite: Writing single {TableName} record, EntityId={EntityId}, UserId={UserId}",
            tableName,
            entity.Id,
            userId);

        // Validate table exists
        try
        {
            _ = _metadataCache.GetEntityType(tableName);
        }
        catch (InvalidOperationException)
        {
            throw new ArgumentException($"Table {tableName} is not registered for sync", nameof(T));
        }

        // Validate tenant authorization if multi-tenant
        if (entity is IMultiTenantSyncEntity multiTenantEntity)
        {
            await ValidateSingleEntityTenantAsync(userId, tenantId, multiTenantEntity.TenantId);
        }

        // Create session and write record in transaction
        var sessionId = Guid.NewGuid();
        var committedAt = DateTime.UtcNow;

        await _serverDb.ExecuteInTransactionAsync(async (transaction) =>
        {
            var session = new SessionRecord
            {
                SessionId = sessionId,
                Status = SyncConstants.STATUS_STAGING,
                SessionType = SyncConstants.SESSION_TYPE_DIRECT,
                CreatedAtUtc = committedAt,
                LastActivityUtc = committedAt
            };
            await _serverDb.CreateSessionAsync(session, transaction);

            var records = new List<object> { entity };
            await WriteRecordsToTableAsync(tableName, records, sessionId, userId, tenantId, transaction);

            await _serverDb.UpdateSessionStatusAsync(sessionId, SyncConstants.STATUS_COMMITTED, transaction, syncVersion: null);
        });

        _logger.LogInformation(
            "DirectWrite: Single write completed. SessionId={SessionId}, Table={TableName}, EntityId={EntityId}",
            sessionId,
            tableName,
            entity.Id);

        return new DirectWriteResult
        {
            SessionId = sessionId,
            RecordsWritten = 1,
            CommittedAtUtc = committedAt
        };
    }

    /// <inheritdoc />
    public async Task<DirectWriteResult> DeleteAsync<T>(Guid id, string userId, string? tenantId = null)
        where T : ISyncEntity
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Entity ID cannot be empty", nameof(id));

        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID is required", nameof(userId));

        var tableName = TableNameResolver.GetTableName<T>();

        _logger.LogInformation(
            "DirectWrite: Soft deleting {TableName} record, EntityId={EntityId}, UserId={UserId}",
            tableName,
            id,
            userId);

        // Validate table exists
        try
        {
            _ = _metadataCache.GetEntityType(tableName);
        }
        catch (InvalidOperationException)
        {
            throw new ArgumentException($"Table {tableName} is not registered for sync", nameof(T));
        }

        // Resolve tenant GUID for WHERE clause filtering (implicit tenant validation:

        // rows affected = 0 when tenantId doesn't match → KeyNotFoundException below)
        Guid? tenantGuid = null;
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            if (!Guid.TryParse(tenantId, out var parsedTenant))
                throw new ArgumentException($"Invalid tenant ID format: {tenantId}", nameof(tenantId));
            tenantGuid = parsedTenant;
        }

        var sessionId = Guid.NewGuid();
        var committedAt = DateTime.UtcNow;
        var rowsAffected = 0;

        await _serverDb.ExecuteInTransactionAsync(async (transaction) =>
        {
            var session = new SessionRecord
            {
                SessionId = sessionId,
                Status = SyncConstants.STATUS_STAGING,
                SessionType = SyncConstants.SESSION_TYPE_DIRECT,
                CreatedAtUtc = committedAt,
                LastActivityUtc = committedAt
            };
            await _serverDb.CreateSessionAsync(session, transaction);

            rowsAffected = await _serverDb.SoftDeleteDirectAsync(
                tableName, id, userId, sessionId, tenantGuid, transaction);

            if (rowsAffected == 0)
                throw new KeyNotFoundException(
                    $"Record '{id}' not found in table '{tableName}' or access denied.");

            await _serverDb.UpdateSessionStatusAsync(sessionId, SyncConstants.STATUS_COMMITTED, transaction, syncVersion: null);
        });

        _logger.LogInformation(
            "DirectWrite: Soft delete completed. SessionId={SessionId}, Table={TableName}, EntityId={EntityId}",
            sessionId,
            tableName,
            id);

        return new DirectWriteResult
        {
            SessionId = sessionId,
            RecordsWritten = rowsAffected,
            CommittedAtUtc = committedAt
        };
    }

    /// <inheritdoc />
    public async Task<DirectWriteResult> DeleteAsync(
        string tableName, Guid id, string userId, string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name is required", nameof(tableName));

        if (id == Guid.Empty)
            throw new ArgumentException("Entity ID cannot be empty", nameof(id));

        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID is required", nameof(userId));

        // Validate table exists
        try
        {
            _ = _metadataCache.GetEntityType(tableName);
        }
        catch (InvalidOperationException)
        {
            throw new ArgumentException($"Unknown table name: {tableName}", nameof(tableName));
        }

        _logger.LogInformation(
            "DirectWrite: Soft deleting {TableName} record, EntityId={EntityId}, UserId={UserId}",
            tableName, id, userId);

        Guid? tenantGuid = null;
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            if (!Guid.TryParse(tenantId, out var parsedTenant))
                throw new ArgumentException($"Invalid tenant ID format: {tenantId}", nameof(tenantId));
            tenantGuid = parsedTenant;
        }

        var sessionId = Guid.NewGuid();
        var committedAt = DateTime.UtcNow;
        var rowsAffected = 0;

        await _serverDb.ExecuteInTransactionAsync(async (transaction) =>
        {
            var session = new SessionRecord
            {
                SessionId = sessionId,
                Status = SyncConstants.STATUS_STAGING,
                SessionType = SyncConstants.SESSION_TYPE_DIRECT,
                CreatedAtUtc = committedAt,
                LastActivityUtc = committedAt
            };
            await _serverDb.CreateSessionAsync(session, transaction);

            rowsAffected = await _serverDb.SoftDeleteDirectAsync(
                tableName, id, userId, sessionId, tenantGuid, transaction);

            if (rowsAffected == 0)
                throw new KeyNotFoundException(
                    $"Record '{id}' not found in table '{tableName}' or access denied.");

            await _serverDb.UpdateSessionStatusAsync(
                sessionId, SyncConstants.STATUS_COMMITTED, transaction, syncVersion: null);
        });

        _logger.LogInformation(
            "DirectWrite: Soft delete completed. SessionId={SessionId}, Table={TableName}, EntityId={EntityId}",
            sessionId, tableName, id);

        return new DirectWriteResult
        {
            SessionId = sessionId,
            RecordsWritten = rowsAffected,
            CommittedAtUtc = committedAt
        };
    }

    /// <summary>
    /// Write records to a specific table using direct upsert.
    /// Sets ModifiedByUserId, SyncSessionId, and ModifiedAtUtc on all records before writing.
    /// </summary>
    /// <param name="tableName">Business table name</param>
    /// <param name="records">List of entities to write</param>
    /// <param name="sessionId">Session ID for sync tracking</param>
    /// <param name="userId">User ID for audit trail</param>
    /// <param name="tenantId">Optional tenant ID (currently unused, reserved for future validation)</param>
    /// <returns>Number of records successfully written</returns>
    private async Task<int> WriteRecordsToTableAsync(
        string tableName,
        List<object> records,
        Guid sessionId,
        string userId,
        string? tenantId,
        IDbTransaction transaction)
    {
        // Set sync metadata on each entity
        var now = DateTime.UtcNow;
        foreach (var record in records)
        {
            if (record is ISyncEntity syncEntity)
            {
                syncEntity.ModifiedByUserId = userId;
                syncEntity.SyncSessionId = sessionId;
                syncEntity.ModifiedAtUtc = now;
            }
        }

        _logger.LogDebug(
            "DirectWrite: Writing {RecordCount} records to {TableName} using direct upsert",
            records.Count,
            tableName);

        // Direct upsert (no temp table overhead)
        var recordsWritten = await _serverDb.UpsertDirectAsync(tableName, records, sessionId, transaction);

        _logger.LogInformation(
            "DirectWrite: Successfully wrote {RecordCount} records to {TableName}, SessionId={SessionId}",
            recordsWritten,
            tableName,
            sessionId);

        return recordsWritten;
    }

    /// <summary>
    /// Validate tenant authorization for all multi-tenant entities in the batch.
    /// </summary>
    private async Task ValidateTenantAuthorizationAsync(
        Dictionary<string, List<object>> tableRecords,
        string userId,
        string? tenantId)
    {
        foreach (var (tableName, records) in tableRecords)
        {
            foreach (var record in records)
            {
                if (record is IMultiTenantSyncEntity multiTenantEntity)
                {
                    await ValidateSingleEntityTenantAsync(userId, tenantId, multiTenantEntity.TenantId);
                }
            }
        }
    }

    /// <summary>
    /// Validate tenant authorization for a single entity.
    /// </summary>
    private async Task ValidateSingleEntityTenantAsync(string userId, string? userTenantId, Guid entityTenantId)
    {
        var entityTenantIdString = entityTenantId.ToString();

        // If user provided tenantId claim, ensure it matches entity
        if (!string.IsNullOrWhiteSpace(userTenantId))
        {
            if (userTenantId != entityTenantIdString)
            {
                _logger.LogWarning(
                    "DirectWrite: Tenant mismatch. UserId={UserId}, UserTenantId={UserTenantId}, EntityTenantId={EntityTenantId}",
                    userId,
                    userTenantId,
                    entityTenantIdString);

                throw new InvalidOperationException(
                    $"Tenant mismatch: user tenant '{userTenantId}' does not match entity tenant '{entityTenantIdString}'");
            }
        }

        // Validate via tenant validator
        var isAuthorized = await _tenantValidator.IsAuthorizedAsync(userId, entityTenantIdString);
        if (!isAuthorized)
        {
            _logger.LogWarning(
                "DirectWrite: Tenant authorization failed. UserId={UserId}, EntityTenantId={EntityTenantId}",
                userId,
                entityTenantIdString);

            throw new InvalidOperationException(
                $"User '{userId}' is not authorized to access tenant '{entityTenantIdString}'");
        }
    }
}
