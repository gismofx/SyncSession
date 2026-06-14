using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;

namespace SyncSession.Client.Seeding;

/// <summary>
/// Built-in <see cref="ISeedDatabaseWriter"/> for consumers using <see cref="IClientDatabase"/>.
/// Deserializes incoming row dictionaries into typed entities and delegates to
/// <see cref="IClientDatabase.UpsertBatchAsync{T}"/> via the registered entity type.
/// </summary>
/// <remarks>
/// Each table is written in a single <see cref="IClientDatabase.UpsertBatchAsync{T}"/> call per batch.
/// Tenant isolation is enforced by passing <paramref name="tenantId"/> through to every upsert.
/// </remarks>
public sealed class ClientDatabaseSeedWriter : ISeedDatabaseWriter
{
    private readonly IClientDatabase _clientDatabase;
    private readonly ITableMetadataCache _metadataCache;
    private readonly Guid? _tenantId;
    private readonly ILogger<ClientDatabaseSeedWriter> _logger;

    /// <summary>Initializes a new instance of <see cref="ClientDatabaseSeedWriter"/>.</summary>
    /// <param name="clientDatabase">Client database to write into.</param>
    /// <param name="metadataCache">Metadata cache for entity type resolution.</param>
    /// <param name="tenantId">Tenant ID for multi-tenant isolation; null for single-tenant.</param>
    /// <param name="logger">Logger.</param>
    public ClientDatabaseSeedWriter(
        IClientDatabase clientDatabase,
        ITableMetadataCache metadataCache,
        Guid? tenantId,
        ILogger<ClientDatabaseSeedWriter> logger)
    {
        _clientDatabase = clientDatabase;
        _metadataCache = metadataCache;
        _tenantId = tenantId;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task BeginTableAsync(string tableName, int totalRows, CancellationToken ct = default)
    {
        _logger.LogDebug("Seed writer: starting table {Table} (~{Total} rows)", tableName, totalRows);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task WriteRowsAsync(
        string tableName,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        var entityType = _metadataCache.GetEntityType(tableName);

        // Deserialize each dict → typed ISyncEntity via EntityReflectionHelper
        var entities = rows
            .Select(d => EntityReflectionHelper.DictionaryToEntity(d, entityType))
            .ToList();

        // Invoke UpsertBatchAsync<T> via reflection — entity type is only known at runtime
        var method = typeof(IClientDatabase)
            .GetMethod(nameof(IClientDatabase.UpsertBatchAsync))!
            .MakeGenericMethod(entityType);

        await (Task)method.Invoke(_clientDatabase, [entities, _tenantId, null])!;
    }

    /// <inheritdoc/>
    public Task EndTableAsync(string tableName, CancellationToken ct = default)
    {
        _logger.LogDebug("Seed writer: finished table {Table}", tableName);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        // Bind the local database to the seeded tenant. From here on, the sync engine rejects
        // any attempt to sync this database under a different tenant. No-op for single-tenant.
        if (_tenantId is Guid tenantId)
            await _clientDatabase.SetBoundTenantAsync(tenantId);
    }
}
