using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;

namespace SyncSession.Core.Models;

/// <summary>
/// Abstract base class for SyncSystem configuration.
/// Owns the table registry shared by client and server.
/// Concrete subclasses add configuration properties specific to their side:
/// <see cref="ClientSyncConfiguration"/> and <c>ServerSyncConfiguration</c>.
/// </summary>
public abstract class SyncConfiguration
{
    // Table configuration dictionary (keyed by table name for fast lookup)
    private readonly Dictionary<string, TableConfig> _tables = new();

    // Seed-only tables are stored separately so Initialize() never sees non-ISyncEntity types
    private readonly Dictionary<string, TableConfig> _seedOnlyTables = new();

    /// <summary>
    /// Configuration for each table to be synchronized (excludes SeedOnly tables).
    /// Key: Table name, Value: Table configuration with entity type, priority, settings, and handler.
    /// </summary>
    public IReadOnlyDictionary<string, TableConfig> Tables => _tables;

    /// <summary>
    /// Manually register a table for syncing (generic type-safe approach).
    /// </summary>
    /// <typeparam name="T">Entity type that implements <see cref="ISyncEntity"/>.</typeparam>
    /// <param name="tableName">Optional table name override (uses <c>[SyncTable]</c> attribute if null).</param>
    /// <param name="priority">Optional priority override (uses <c>[SyncTable]</c> attribute if null).</param>
    /// <param name="enabled">Whether the table is enabled for syncing (default: true).</param>
    public void RegisterTable<T>(string? tableName = null, int? priority = null, bool enabled = true)
        where T : ISyncEntity
    {
        RegisterTableCore(typeof(T), tableName, priority, enabled);
    }

    /// <summary>
    /// Register a table for seeding/streaming without requiring <see cref="ISyncEntity"/>.
    /// Use for legacy entities that have sync columns in the DB but don't implement the interface.
    /// </summary>
    public void RegisterTable(Type entityType, string? tableName = null, int? priority = null, bool enabled = true)
    {
        RegisterTableCore(entityType, tableName, priority, enabled);
    }

    private void RegisterTableCore(Type type, string? tableName, int? priority, bool enabled)
    {
        var resolvedTableName = tableName ?? TableNameResolver.GetTableName(type);
        var attribute = type.GetCustomAttribute<SyncTableAttribute>();

        _tables[resolvedTableName] = new TableConfig
        {
            EntityType = type,
            TableName = resolvedTableName,
            Priority = priority ?? attribute?.Priority ?? 0,
            Enabled = enabled
        };
    }

    /// <summary>
    /// Register a table for seeding/streaming only. Does not require <see cref="ISyncEntity"/>.
    /// Use for legacy entities that have sync columns in the DB but don't implement the interface.
    /// These tables are excluded from push/pull operations.
    /// </summary>
    /// <param name="entityType">Entity type representing the table schema.</param>
    /// <param name="tableName">Table name (defaults to <paramref name="entityType"/> name).</param>
    /// <param name="priority">Streaming priority (lower = streamed first).</param>
    /// <param name="tenantFiltered">
    /// When <c>true</c>, snapshot creation filters rows by <c>TenantId</c>.
    /// Set this for any table whose data is tenant-scoped but does not implement
    /// <see cref="IMultiTenantSyncEntity"/>.
    /// </param>
    public void RegisterSeedOnlyTable(Type entityType, string? tableName = null, int? priority = null, bool tenantFiltered = false)
    {
        var resolvedTableName = tableName ?? entityType.Name;
        _seedOnlyTables[resolvedTableName] = new TableConfig
        {
            EntityType = entityType,
            TableName = resolvedTableName,
            Priority = priority ?? 0,
            Enabled = true,
            SeedOnly = true,
            TenantFiltered = tenantFiltered
        };
    }

    /// <summary>
    /// Auto-discover and register all types with <c>[SyncTable]</c> attribute in the given assembly.
    /// Already-registered tables are skipped, including tables registered via
    /// <see cref="RegisterSeedOnlyTable"/> — explicit registration always takes precedence
    /// over auto-discovery.
    /// </summary>
    /// <param name="assembly">Assembly to scan for syncable entities.</param>
    public void DiscoverAndRegisterTables(Assembly assembly)
    {
        var syncEntityTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ISyncEntity).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<SyncTableAttribute>() != null);

        foreach (var type in syncEntityTypes)
        {
            var attribute = type.GetCustomAttribute<SyncTableAttribute>()!;
            var tableName = TableNameResolver.GetTableName(type);

            if (_tables.ContainsKey(tableName) || _seedOnlyTables.ContainsKey(tableName))
                continue;

            _tables[tableName] = new TableConfig
            {
                EntityType = type,
                TableName = tableName,
                Priority = attribute.Priority,
                Enabled = true
            };
        }
    }

    /// <summary>
    /// Get all registered tables in priority order. Excludes SeedOnly tables.
    /// </summary>
    public IEnumerable<TableConfig> GetTables() => _tables.Values.OrderBy(t => t.Priority);

    /// <summary>
    /// Get all registered tables including SeedOnly tables, in priority order.
    /// Used by SeedService which streams all tables regardless of SeedOnly flag.
    /// </summary>
    public IEnumerable<TableConfig> GetAllTables() =>
        _tables.Values.Concat(_seedOnlyTables.Values).OrderBy(t => t.Priority);

    /// <summary>
    /// Get table configuration by table name.
    /// </summary>
    public TableConfig? GetTableConfig(string tableName) => _tables.GetValueOrDefault(tableName);

    /// <summary>
    /// Get table configuration by entity type.
    /// </summary>
    public TableConfig? GetTableConfig<T>() where T : ISyncEntity =>
        _tables.Values.FirstOrDefault(t => t.EntityType == typeof(T));

    /// <summary>
    /// Get table configuration by entity type.
    /// </summary>
    public TableConfig? GetTableConfig(Type entityType) =>
        _tables.Values.FirstOrDefault(t => t.EntityType == entityType);

    /// <summary>
    /// Validates configuration values and throws <see cref="InvalidOperationException"/>
    /// if any are invalid. Each subclass validates its own properties.
    /// </summary>
    public abstract void Validate();
}
