using System.Reflection;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;

namespace SyncSession.Core.Services;

/// <summary>
/// Provides cached metadata for table operations.
/// Pre-computes all column lists at construction using reflection (one-time cost).
/// Runtime lookups are O(1) dictionary access (zero reflection).
/// Thread-safe after construction.
/// </summary>
public class TableMetadataCache : ITableMetadataCache
{
    private readonly Dictionary<string, CachedTableMetadata> _cache;

    public TableMetadataCache(SyncConfiguration config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        _cache = new Dictionary<string, CachedTableMetadata>(StringComparer.OrdinalIgnoreCase);

        // Build entire cache at construction (one-time reflection cost)
        foreach (var tableConfig in config.Tables.Values)
        {
            var metadata = new CachedTableMetadata
            {
                EntityType = tableConfig.EntityType,
                ServerSelectColumns = InvokeGenericHelper(
                    nameof(EntityReflectionHelper.GetColumnsForServerSelect),
                    tableConfig.EntityType),
                ServerUpsertColumns = InvokeGenericHelper(
                    nameof(EntityReflectionHelper.GetColumnsForServerUpsert),
                    tableConfig.EntityType),
                DirectUpsertColumns = InvokeGenericHelper(
                    nameof(EntityReflectionHelper.GetColumnsForDirectUpsert),
                    tableConfig.EntityType),
                PullUpsertColumns = InvokeGenericHelper(
                    nameof(EntityReflectionHelper.GetColumnsForPullUpsert),
                    tableConfig.EntityType),
                PushSelectColumns = InvokeGenericHelper(
                    nameof(EntityReflectionHelper.GetColumnsForPushSelect),
                    tableConfig.EntityType),
                IsMultiTenant = typeof(IMultiTenantSyncEntity).IsAssignableFrom(tableConfig.EntityType),
                ValidPushColumns = new HashSet<string>(
                    InvokeGenericHelper(
                        nameof(EntityReflectionHelper.GetColumnsForServerUpsert),
                        tableConfig.EntityType),
                    StringComparer.OrdinalIgnoreCase)
            };

            _cache[tableConfig.TableName] = metadata;
        }
    }

    /// <summary>
    /// Invokes a generic EntityReflectionHelper method via reflection (one-time cost at startup).
    /// </summary>
    private static List<string> InvokeGenericHelper(string methodName, Type entityType)
    {
        var method = typeof(EntityReflectionHelper)
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
            ?.MakeGenericMethod(entityType);

        if (method == null)
            throw new InvalidOperationException(
                $"Could not find method {methodName} on EntityReflectionHelper");

        return (List<string>)method.Invoke(null, null)!;
    }

    /// <inheritdoc/>
    public List<string> GetColumnsForServerSelect(string tableName)
    {
        return GetMetadata(tableName).ServerSelectColumns;
    }

    /// <inheritdoc/>
    public List<string> GetColumnsForServerUpsert(string tableName)
    {
        return GetMetadata(tableName).ServerUpsertColumns;
    }

    /// <inheritdoc/>
    public List<string> GetColumnsForDirectUpsert(string tableName)
    {
        return GetMetadata(tableName).DirectUpsertColumns;
    }

    /// <inheritdoc/>
    public List<string> GetColumnsForPullUpsert(string tableName)
    {
        return GetMetadata(tableName).PullUpsertColumns;
    }

    /// <inheritdoc/>
    public List<string> GetColumnsForPushSelect(string tableName)
    {
        return GetMetadata(tableName).PushSelectColumns;
    }

    /// <inheritdoc/>
    public Type GetEntityType(string tableName)
    {
        return GetMetadata(tableName).EntityType;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> GetValidPushColumns(string tableName)
    {
        // Pre-built OrdinalIgnoreCase set: handles JSON naming policy mismatches (e.g. camelCase
        // keys from PostAsJsonAsync vs PascalCase property names from reflection).
        return GetMetadata(tableName).ValidPushColumns;
    }

    /// <inheritdoc/>
    public bool IsMultiTenant(string tableName)
    {
        return GetMetadata(tableName).IsMultiTenant;
    }

    private CachedTableMetadata GetMetadata(string tableName)
    {
        if (!_cache.TryGetValue(tableName, out var metadata))
            throw new InvalidOperationException(
                $"Table '{tableName}' not registered in SyncConfiguration. " +
                $"Ensure entity has [SyncTable] attribute and is discovered via " +
                $"SyncConfiguration.DiscoverAndRegisterTables().");

        return metadata;
    }

    /// <summary>
    /// Internal cache model.
    /// </summary>
    private class CachedTableMetadata
    {
        public required Type EntityType { get; init; }
        public required List<string> ServerSelectColumns { get; init; }
        public required List<string> ServerUpsertColumns { get; init; }
        public required List<string> DirectUpsertColumns { get; init; }
        public required List<string> PullUpsertColumns { get; init; }
        public required List<string> PushSelectColumns { get; init; }
        public required IReadOnlySet<string> ValidPushColumns { get; init; }
        public required bool IsMultiTenant { get; init; }
    }
}
