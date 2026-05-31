using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;

namespace SyncSession.Samples.Shared.Schema;

/// <summary>
/// Generates SQLite CREATE TABLE DDL from ISyncEntity types via reflection.
/// Single source of truth for SQLite schema — used by benchmarks, sample apps, and tests.
/// </summary>
public static class SqliteSchemaHelper
{
    /// <summary>
    /// Generate CREATE TABLE SQL for a single entity type.
    /// Includes standard sync indexes (IsDirty, SyncSessionId, ModifiedByUserId).
    /// </summary>
    public static string GetCreateTableSql<T>() where T : ISyncEntity
    {
        return GetCreateTableSql(typeof(T));
    }

    /// <summary>
    /// Generate CREATE TABLE SQL for a single entity type (non-generic).
    /// </summary>
    public static string GetCreateTableSql(Type entityType)
    {
        var tableName = GetTableName(entityType);
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE IF NOT EXISTS {tableName} (");

        var columnDefs = new List<string>();
        foreach (var prop in properties)
        {
            // Skip properties marked with [SyncColumn(Ignore = true)]
            var syncCol = prop.GetCustomAttribute<SyncColumnAttribute>();
            if (syncCol?.Ignore == true) continue;

            var columnName = syncCol?.ColumnName ?? prop.Name;
            var sqliteType = MapToSqliteType(prop.PropertyType);
            var nullable = IsNullable(prop.PropertyType);
            var isPrimaryKey = prop.Name == "Id";

            var def = $"    {columnName} {sqliteType}";

            if (isPrimaryKey)
                def += " PRIMARY KEY";
            else if (!nullable)
                def += " NOT NULL";

            // Add defaults for sync infrastructure
            if (prop.Name == "IsDirty")
                def += " DEFAULT 0";
            else if (prop.Name == "IsDeleted")
                def += " DEFAULT 0";

            columnDefs.Add(def);
        }

        sb.AppendLine(string.Join(",\n", columnDefs));
        sb.AppendLine(");");

        // Standard sync indexes
        sb.AppendLine();
        sb.AppendLine($"CREATE INDEX IF NOT EXISTS IX_{tableName}_Dirty ON {tableName}(IsDirty) WHERE IsDirty = 1;");
        sb.AppendLine($"CREATE INDEX IF NOT EXISTS IX_{tableName}_Session ON {tableName}(SyncSessionId);");
        sb.AppendLine($"CREATE INDEX IF NOT EXISTS IX_{tableName}_User ON {tableName}(ModifiedByUserId);");

        return sb.ToString();
    }

    /// <summary>
    /// Generate CREATE TABLE SQL for all standard sample entities
    /// (Products, Customers, Orders, OrderItems) plus LocalSyncState.
    /// Tables are ordered by priority (FK-safe).
    /// </summary>
    public static string GetCreateAllTablesSql(Assembly? entitiesAssembly = null)
    {
        var assembly = entitiesAssembly ?? typeof(Entities.Customer).Assembly;
        var entityTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface
                        && typeof(ISyncEntity).IsAssignableFrom(t)
                        && t.GetCustomAttribute<SyncTableAttribute>() != null)
            .OrderBy(t => t.GetCustomAttribute<SyncTableAttribute>()!.Priority)
            .ToList();

        var sb = new StringBuilder();
        foreach (var type in entityTypes)
        {
            sb.AppendLine(GetCreateTableSql(type));
            sb.AppendLine();
        }

        // LocalSyncState table (used by SqliteClientDatabase)
        sb.AppendLine(@"CREATE TABLE IF NOT EXISTS LocalSyncState (
    TableName TEXT PRIMARY KEY,
    LastSyncVersion INTEGER NOT NULL DEFAULT 0,
    LastSyncCompletedAtUtc TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'
);");

        return sb.ToString();
    }

    /// <summary>
    /// Map .NET type to SQLite column type.
    /// </summary>
    private static string MapToSqliteType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(Guid))
            return "TEXT";
        if (underlying == typeof(string))
            return "TEXT";
        if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(bool))
            return "INTEGER";
        if (underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float))
            return "REAL";
        if (underlying == typeof(DateTime))
            return "TEXT";

        // Fallback
        return "TEXT";
    }

    /// <summary>
    /// Check if a property type is nullable (reference type or Nullable&lt;T&gt;).
    /// </summary>
    private static bool IsNullable(Type type)
    {
        if (!type.IsValueType)
            return true; // Reference types are nullable
        return Nullable.GetUnderlyingType(type) != null; // Nullable<T>
    }

    /// <summary>
    /// Get table name from [SyncTable] attribute.
    /// </summary>
    private static string GetTableName(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<SyncTableAttribute>();
        if (attr == null)
            throw new InvalidOperationException(
                $"Type {entityType.Name} must have [SyncTable] attribute.");
        return attr.TableName;
    }
}
