using System;
using System.Collections.Concurrent;
using System.Reflection;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;

namespace SyncSession.Core.Utilities;

/// <summary>
/// Single source of truth for resolving database table names from <see cref="SyncTableAttribute"/>.
/// All table name resolution — registration, discovery, and runtime SQL generation — must
/// flow through this class to prevent divergence between configuration and query paths.
/// </summary>
public static class TableNameResolver
{
    private static readonly ConcurrentDictionary<Type, string> _tableNameCache = new();

    /// <summary>
    /// Get table name for an entity type (cached for performance)
    /// </summary>
    /// <typeparam name="T">Entity type with [SyncTable] attribute</typeparam>
    /// <returns>Table name from attribute</returns>
    /// <exception cref="InvalidOperationException">If type doesn't have [SyncTable] attribute</exception>
    public static string GetTableName<T>() where T : ISyncEntity
    {
        return GetTableName(typeof(T));
    }

    /// <summary>
    /// Get table name for an entity type (cached for performance).
    /// Returns <see cref="SyncTableAttribute.TableName"/> if explicitly set,
    /// otherwise falls back to the class name.
    /// </summary>
    /// <param name="entityType">Entity type with [SyncTable] attribute</param>
    /// <returns>Table name from attribute, or class name if attribute has no explicit name</returns>
    /// <exception cref="InvalidOperationException">If type doesn't have [SyncTable] attribute</exception>
    public static string GetTableName(Type entityType)
    {
        return _tableNameCache.GetOrAdd(entityType, type =>
        {
            var attribute = type.GetCustomAttribute<SyncTableAttribute>();
            
            if (attribute == null)
            {
                throw new InvalidOperationException(
                    $"Type {type.Name} must have [SyncTable] attribute to be synchronized. " +
                    $"Example: [SyncTable(\"TableName\", Priority = 1)]");
            }
            
            return attribute.TableName ?? type.Name;
        });
    }

    /// <summary>
    /// Clear the cache (useful for testing)
    /// </summary>
    public static void ClearCache()
    {
        _tableNameCache.Clear();
    }
}
