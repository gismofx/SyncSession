using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Dapper;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Core.Utilities;

/// <summary>
/// Provides cached, reflection-based utilities for column discovery, entity serialization,
/// and property classification across client and server database implementations.
/// </summary>
/// <remarks>
/// <para><b>Initialization:</b> Call <see cref="Initialize"/> exactly once at startup with a
/// <see cref="SyncConfiguration"/> to enable table-name overloads. Generic methods work without
/// initialization. Calling <see cref="Initialize"/> twice throws; use <see cref="ClearCache"/>
/// first for test re-initialization.</para>
///
/// <para><b>Caching architecture (two layers):</b></para>
/// <para>
/// Layer 1 — Generic column cache (<c>_columnNamesCache</c>): Keyed by <c>(Type, bool, bool, bool)</c>
/// tuple representing entity type and inclusion flags. Populated lazily on first call to any
/// <c>GetColumnsFor*&lt;T&gt;()</c> method.
/// </para>
/// <para>
/// Layer 2 — Delegate cache (<c>_serverSelectDelegates</c>, <c>_serverUpsertDelegates</c>):
/// Keyed by table name (case-insensitive). Populated eagerly during <see cref="Initialize"/>.
/// The table-name overloads (<c>GetColumnsForServerSelect(string)</c>, <c>GetColumnsForServerUpsert(string)</c>)
/// invoke these delegates, which in turn warm Layer 1 on first use.
/// </para>
/// <para>
/// Additional caches: <c>_propertiesCache</c> (PropertyInfo[] per type),
/// <c>_propertyByNameCache</c> (single property lookup), <c>_allPropertyNamesCache</c> (all names per type).
/// </para>
///
/// <para><b>Thread safety:</b> All caches use <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// <see cref="Initialize"/> uses <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>
/// for single-write safety. Safe for concurrent reads from multiple sync operations.</para>
///
/// <para><b>Unbounded growth:</b> Caches grow with each unique entity type and flag combination
/// but are never evicted during normal operation. For the typical sync scenario (fixed set of
/// entity types), this stabilizes after the first full sync cycle. Call <see cref="ClearCache"/>
/// only in test scenarios.</para>
/// </remarks>
public static class EntityReflectionHelper
{
    // Column name cache — key is (Type, flags) tuple; avoids string allocation and FullName collision risk
    private static readonly ConcurrentDictionary<(Type Type, bool IncludeIsDirty, bool IncludeModifiedAtUtc, bool IncludeSyncSessionId), List<string>> _columnNamesCache = new();
    
    // Cache property info per type to avoid repeated reflection
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propertiesCache = new();
    
    // Cache property info by name for quick lookups
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> _propertyByNameCache = new();
    
    // Cache all property names per type
    private static readonly ConcurrentDictionary<Type, List<string>> _allPropertyNamesCache = new();
    
    // Infrastructure property names from ISyncInfrastructure interface
    private static readonly HashSet<string> _infraPropertyNames;
    
    // Delegate caches for table-name overloads — populated during Initialize(), case-insensitive
    // Eliminates per-call reflection (MakeGenericMethod) in GetColumnsForServerSelect/Upsert(string)
    private static readonly ConcurrentDictionary<string, Func<IReadOnlyList<string>>> _serverSelectDelegates
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Func<IReadOnlyList<string>>> _serverUpsertDelegates
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Func<IReadOnlyList<string>>> _directUpsertDelegates
        = new(StringComparer.OrdinalIgnoreCase);
    
    // Configuration reference for table → type mapping
    private static SyncConfiguration? _config;
    
    
    static EntityReflectionHelper()
    {
        // Populate infrastructure property names via reflection on ISyncInfrastructure
        // This eliminates hard-coded property name strings
        var infraType = typeof(ISyncEntity).GetInterfaces()
            .FirstOrDefault(i => i.Name == "ISyncInfrastructure");
        
        if (infraType != null)
        {
            _infraPropertyNames = infraType.GetProperties()
                .Select(p => p.Name)
                .ToHashSet();
        }
        else
        {
            throw new InvalidOperationException(
                "ISyncInfrastructure interface not found on the ISyncEntity hierarchy. " +
                "Ensure ISyncEntity inherits from ISyncInfrastructure.");
        }
    }
    
    /// <summary>
    /// Clears all reflection caches and resets initialization state.
    /// </summary>
    /// <remarks>
    /// Clears column name caches, property caches, and delegate caches. Also resets
    /// <c>_config</c>, allowing <see cref="Initialize"/> to be called again. Intended
    /// for use in testing; production code should not call this after startup.
    /// </remarks>
    public static void ClearCache()
    {
        _columnNamesCache.Clear();
        _propertiesCache.Clear();
        _propertyByNameCache.Clear();
        _allPropertyNamesCache.Clear();
        _serverSelectDelegates.Clear();
        _serverUpsertDelegates.Clear();
        _directUpsertDelegates.Clear();
        Interlocked.Exchange(ref _config, null);
    }
    
    /// <summary>Gets a value indicating whether <see cref="Initialize"/> has been called.</summary>
    public static bool IsInitialized => _config != null;

    /// <summary>
    /// Initializes <see cref="EntityReflectionHelper"/> with a <see cref="SyncConfiguration"/>
    /// for table-name lookups. Must be called exactly once at application startup before using
    /// table-name overloads of <c>GetColumnsForServer*</c>.
    /// </summary>
    /// <param name="config">The <see cref="SyncConfiguration"/> with discovered tables.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="Initialize"/> has already been called. Call <see cref="ClearCache"/>
    /// first if re-initialization is required (test scenarios only).
    /// </exception>
    public static void Initialize(SyncConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        
        if (Interlocked.CompareExchange(ref _config, config, null) != null)
            throw new InvalidOperationException(
                "EntityReflectionHelper.Initialize has already been called. " +
                "Initialize may only be called once per application lifetime. " +
                "Call ClearCache() first if re-initialization is required (test scenarios only).");
        
        // Pre-build typed delegates for each registered table — one-time reflection cost,
        // eliminates per-call MakeGenericMethod in the table-name overloads.
        var selectBridge  = typeof(EntityReflectionHelper).GetMethod(
            nameof(BridgeGetColumnsForServerSelect), BindingFlags.NonPublic | BindingFlags.Static)!;
        var upsertBridge  = typeof(EntityReflectionHelper).GetMethod(
            nameof(BridgeGetColumnsForServerUpsert), BindingFlags.NonPublic | BindingFlags.Static)!;
        var directUpsertBridge = typeof(EntityReflectionHelper).GetMethod(
            nameof(BridgeGetColumnsForDirectUpsert), BindingFlags.NonPublic | BindingFlags.Static)!;
        
        foreach (var tableConfig in config.Tables.Values)
        {
            var entityType = tableConfig.EntityType;
            var tableName  = tableConfig.TableName;
            
            _serverSelectDelegates[tableName] = (Func<IReadOnlyList<string>>)Delegate.CreateDelegate(
                typeof(Func<IReadOnlyList<string>>), selectBridge.MakeGenericMethod(entityType));
            
            _serverUpsertDelegates[tableName] = (Func<IReadOnlyList<string>>)Delegate.CreateDelegate(
                typeof(Func<IReadOnlyList<string>>), upsertBridge.MakeGenericMethod(entityType));
            
            _directUpsertDelegates[tableName] = (Func<IReadOnlyList<string>>)Delegate.CreateDelegate(
                typeof(Func<IReadOnlyList<string>>), directUpsertBridge.MakeGenericMethod(entityType));
        }
    }
    
    /// <summary>
    /// Bridge method for delegate binding in <see cref="Initialize"/>. Uniquely named to avoid
    /// ambiguity during <see cref="MethodInfo"/> lookup across overloads.
    /// </summary>
    private static IReadOnlyList<string> BridgeGetColumnsForServerSelect<T>() where T : ISyncEntity
        => GetColumnsForServerSelect<T>();
    
    /// <summary>
    /// Bridge method for delegate binding in <see cref="Initialize"/>. Uniquely named to avoid
    /// ambiguity during <see cref="MethodInfo"/> lookup across overloads.
    /// </summary>
    private static IReadOnlyList<string> BridgeGetColumnsForServerUpsert<T>() where T : ISyncEntity
        => GetColumnsForServerUpsert<T>();
    
    /// <summary>
    /// Bridge method for delegate binding in <see cref="Initialize"/>. Uniquely named to avoid
    /// ambiguity during <see cref="MethodInfo"/> lookup across overloads.
    /// </summary>
    private static IReadOnlyList<string> BridgeGetColumnsForDirectUpsert<T>() where T : ISyncEntity
        => GetColumnsForDirectUpsert<T>();
    
    
    /// <summary>
    /// Gets columns for server SELECT during PULL operations (table name lookup).
    /// Requires <see cref="Initialize"/> to have been called with the table's entity type registered.
    /// </summary>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <returns>Column names including business + sync columns for server SELECT.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="Initialize"/> has not been called, or <paramref name="tableName"/>
    /// is not registered in the <see cref="SyncConfiguration"/>.
    /// </exception>
    public static IReadOnlyList<string> GetColumnsForServerSelect(string tableName)
    {
        if (_serverSelectDelegates.TryGetValue(tableName, out var fn))
            return fn();
        
        ThrowForUnknownTable(tableName);
        throw new InvalidOperationException(); // unreachable — ThrowForUnknownTable always throws
    }
    
    /// <summary>
    /// Gets columns for server UPSERT during PUSH operations (table name lookup).
    /// Requires <see cref="Initialize"/> to have been called with the table's entity type registered.
    /// </summary>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <returns>Column names for server-side upsert from temp tables.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="Initialize"/> has not been called, or <paramref name="tableName"/>
    /// is not registered in the <see cref="SyncConfiguration"/>.
    /// </exception>
    public static IReadOnlyList<string> GetColumnsForServerUpsert(string tableName)
    {
        if (_serverUpsertDelegates.TryGetValue(tableName, out var fn))
            return fn();
        
        ThrowForUnknownTable(tableName);
        throw new InvalidOperationException(); // unreachable
    }
    
    /// <summary>
    /// Gets columns for direct upsert operations (Direct Write API).
    /// Requires <see cref="Initialize"/> to have been called with the table's entity type registered.
    /// </summary>
    /// <param name="tableName">Business table name (e.g., "Customers").</param>
    /// <returns>Column names for direct upsert including SyncSessionId.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="Initialize"/> has not been called, or <paramref name="tableName"/>
    /// is not registered in the <see cref="SyncConfiguration"/>.
    /// </exception>
    public static IReadOnlyList<string> GetColumnsForDirectUpsert(string tableName)
    {
        if (_directUpsertDelegates.TryGetValue(tableName, out var fn))
            return fn();
        
        ThrowForUnknownTable(tableName);
        throw new InvalidOperationException(); // unreachable
    }
    
    /// <summary>
    /// Throws a descriptive <see cref="InvalidOperationException"/> for unresolved table-name lookups.
    /// </summary>
    private static void ThrowForUnknownTable(string tableName)
    {
        if (_config == null)
            throw new InvalidOperationException(
                "EntityReflectionHelper not initialized. " +
                "Call EntityReflectionHelper.Initialize(config) at application startup.");
        
        throw new InvalidOperationException(
            $"Table '{tableName}' not registered in SyncConfiguration. " +
            "Ensure entity has [SyncTable] attribute and is discovered via assembly scanning.");
    }

    /// <summary>
    /// Gets columns for client upsert during PULL operations (server → client).
    /// Includes: Business columns, IsDirty, ModifiedAtUtc, IsDeleted, ModifiedByUserId
    /// Excludes: SyncSessionId (not needed for client storage)
    /// </summary>
    public static IReadOnlyList<string> GetColumnsForPullUpsert<T>() where T : ISyncEntity
    {
        return GetColumns<T>(
            includeIsDirty: true,
            includeModifiedAtUtc: true,
            includeSyncSessionId: false
        );
    }

    /// <summary>
    /// Gets columns for client SELECT during PUSH operations (client → server).
    /// Includes: Business columns, IsDeleted, ModifiedByUserId, ModifiedAtUtc
    /// Excludes: IsDirty (client-only), SyncSessionId (server assigns)
    /// </summary>
    /// <remarks>
    /// ModifiedAtUtc is included so the client's actual edit timestamp is preserved
    /// through the push pipeline. The server no longer overwrites it with its own clock.
    /// SessionRecord.CommittedAtUtc tracks when the server processed the session.
    /// </remarks>
    public static IReadOnlyList<string> GetColumnsForPushSelect<T>() where T : ISyncEntity
    {
        return GetColumns<T>(
            includeIsDirty: false,
            includeModifiedAtUtc: true,   // Client owns this timestamp
            includeSyncSessionId: false
        );
    }

    /// <summary>
    /// Gets all columns that exist in the client SQLite table.
    /// Used for SELECT queries against the local database (e.g., GetDirtyRecordsAsync).
    /// Includes: Business columns, IsDirty, ModifiedAtUtc, IsDeleted, ModifiedByUserId
    /// Excludes: SyncSessionId (not stored on client)
    /// </summary>
    public static IReadOnlyList<string> GetColumnsForClientSelect<T>() where T : ISyncEntity
    {
        return GetColumns<T>(
            includeIsDirty: true,
            includeModifiedAtUtc: true,
            includeSyncSessionId: false   // Not in client SQLite tables
        );
    }

    /// <summary>
    /// Gets columns for server upsert during PUSH operations (client → server).
    /// Includes: Business columns, IsDeleted, ModifiedByUserId, ModifiedAtUtc
    /// Excludes: IsDirty (client-only), SyncSessionId (server assigns during upsert)
    /// </summary>
    /// <remarks>
    /// ModifiedAtUtc is included so the client's actual edit timestamp flows through the temp
    /// table into the production table. The server no longer overwrites it with UTC_TIMESTAMP().
    /// SessionRecord.CommittedAtUtc is the authoritative server-side processing timestamp.
    /// </remarks>
    public static IReadOnlyList<string> GetColumnsForServerUpsert<T>() where T : ISyncEntity
    {
        return GetColumns<T>(
            includeIsDirty: false,        // Client-only property
            includeModifiedAtUtc: true,   // Client owns this timestamp
            includeSyncSessionId: false   // Server assigns this during upsert
        );
    }

    /// <summary>
    /// Gets columns for direct upsert operations (Direct Write API, Session 28).
    /// Includes: Business columns, IsDeleted, ModifiedByUserId, ModifiedAtUtc, SyncSessionId
    /// Excludes: IsDirty (client-only)
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetColumnsForServerUpsert{T}"/> (temp table path), this includes
    /// SyncSessionId because DirectWriteService sets it on the entity before calling upsert.
    /// The temp table path excludes it because SQL appends SyncSessionId outside the column list.
    /// </remarks>
    public static IReadOnlyList<string> GetColumnsForDirectUpsert<T>() where T : ISyncEntity
    {
        return GetColumns<T>(
            includeIsDirty: false,        // Client-only property
            includeModifiedAtUtc: true,   // DirectWriteService sets this on entity
            includeSyncSessionId: true    // DirectWriteService sets this on entity
        );
    }

    /// <summary>
    /// Gets columns for server SELECT during PULL operations (server → client).
    /// Includes: Business columns, ModifiedAtUtc, SyncSessionId, IsDeleted, ModifiedByUserId
    /// Excludes: IsDirty (client-only)
    /// 
    /// CRITICAL: SyncSessionId MUST be included so clients can extract session IDs for tracking.
    /// Without this, clients can't mark sessions as processed, breaking session-based sync.
    /// </summary>
    public static IReadOnlyList<string> GetColumnsForServerSelect<T>() where T : ISyncEntity
    {
        return GetColumns<T>(
            includeIsDirty: false,  // Client-only property
            includeModifiedAtUtc: true,  // Send to client
            includeSyncSessionId: true  // REQUIRED: Client needs this for session tracking
        );
    }


    /// <summary>
    /// Core method that builds column lists based on explicit inclusion flags.
    /// Uses caching for performance.
    /// </summary>
    private static List<string> GetColumns<T>(
        bool includeIsDirty,
        bool includeModifiedAtUtc,
        bool includeSyncSessionId) where T : ISyncEntity
    {
        // Tuple key avoids string allocation and FullName collision risk across assemblies
        var cacheKey = (typeof(T), includeIsDirty, includeModifiedAtUtc, includeSyncSessionId);

        if (_columnNamesCache.TryGetValue(cacheKey, out var cachedColumns))
            return cachedColumns;

        var properties = GetCachedProperties<T>();
        var columns = new List<string>();

        foreach (var prop in properties)
        {
            var syncColumn = prop.GetCustomAttribute<SyncColumnAttribute>();
            if (syncColumn?.Ignore == true)
                continue;

            var propName = prop.Name;

            // Only conditional checks for infrastructure properties
            if (propName == nameof(ISyncInfrastructure.IsDirty) && !includeIsDirty)
                continue;
            if (propName == nameof(ISyncInfrastructure.ModifiedAtUtc) && !includeModifiedAtUtc)
                continue;
            if (propName == nameof(ISyncInfrastructure.SyncSessionId) && !includeSyncSessionId)
                continue;

            // IsDeleted and ModifiedByUserId always included (no check needed)

            var columnName = syncColumn?.ColumnName ?? propName;
            columns.Add(columnName);
        }

        _columnNamesCache[cacheKey] = columns;
        return columns;
    }

    /// <summary>
    /// Checks if a property is client-only (exists only on client databases).
    /// Currently only IsDirty is client-only.
    /// </summary>
    /// <param name="propertyName">The property name to check</param>
    /// <returns>True if the property is client-only</returns>
    public static bool IsClientOnlyProperty(string propertyName)
    {
        return propertyName == nameof(ISyncEntity.IsDirty);
    }

    /// <summary>
    /// Checks if a property is sync infrastructure (auto-managed by the sync system).
    /// Infrastructure properties: IsDirty, ModifiedAtUtc, SyncSessionId
    /// These properties are excluded from business operations and dynamic parameter generation.
    /// Does NOT include: Id, IsDeleted, ModifiedByUserId (business properties preserved during sync)
    /// </summary>
    /// <param name="propertyName">The property name to check</param>
    /// <returns>True if the property is a sync infrastructure property</returns>
    public static bool IsSyncInfrastructureProperty(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return _infraPropertyNames.Contains(propertyName);
    }


    /// <summary>
    /// Creates Dapper dynamic parameters from an entity instance.
    /// Used for building parameterized SQL queries safely.
    /// Uses cached property info for performance.
    /// </summary>
    /// <typeparam name="T">The entity type implementing ISyncEntity</typeparam>
    /// <param name="record">The entity instance</param>
    /// <param name="sessionId">The sync session ID to include</param>
    /// <param name="sqliteDateTimeHandling">If true, converts DateTime to ISO 8601 strings for SQLite</param>
    /// <returns>DynamicParameters object for use with Dapper</returns>
    public static DynamicParameters CreateDynamicParameters<T>(
        T record,
        Guid sessionId,
        bool sqliteDateTimeHandling = false) where T : ISyncEntity
    {
        var properties = GetCachedProperties<T>();
        var parameters = new Dictionary<string, object?>();

        foreach (var prop in properties)
        {
            // Check for [SyncColumn(Ignore = true)]
            var syncColumn = prop.GetCustomAttribute<SyncColumnAttribute>();
            if (syncColumn?.Ignore == true)
                continue;

            // Skip infrastructure properties (IsDirty, ModifiedAtUtc, SyncSessionId)
            // Keep Id and business properties (IsDeleted, ModifiedByUserId)
            if (IsSyncInfrastructureProperty(prop.Name))
                continue;

            // Use custom column name if specified
            var columnName = syncColumn?.ColumnName ?? prop.Name;
            var value = prop.GetValue(record);

            // Handle SQLite DateTime conversion to ISO 8601
            if (sqliteDateTimeHandling && value is DateTime dt)
            {
                parameters[columnName] = dt.ToString("O");
            }
            else
            {
                parameters[columnName] = value;
            }
        }

        // Add sync session ID
        parameters["SyncSessionId"] = sessionId.ToString();

        return new DynamicParameters(parameters);
    }

    /// <summary>
    /// Gets all property names from an entity type.
    /// Useful for debugging and logging.
    /// Results are cached for performance.
    /// </summary>
    /// <typeparam name="T">The entity type implementing ISyncEntity</typeparam>
    /// <returns>List of all public property names</returns>
    public static IReadOnlyList<string> GetAllPropertyNames<T>() where T : ISyncEntity
    {
        var type = typeof(T);
        
        // Return cached result if available
        if (_allPropertyNamesCache.TryGetValue(type, out var cachedNames))
        {
            return cachedNames;
        }

        // Get properties and extract names
        var properties = GetCachedProperties<T>();
        var names = properties.Select(p => p.Name).ToList();
        
        // Cache and return
        _allPropertyNamesCache[type] = names;
        return names;
    }

    /// <summary>
    /// Gets the value of a property from an entity instance.
    /// Uses cached property info for performance.
    /// </summary>
    /// <typeparam name="T">The entity type implementing ISyncEntity</typeparam>
    /// <param name="record">The entity instance</param>
    /// <param name="propertyName">The property name</param>
    /// <returns>The property value, or null if not found</returns>
    public static object? GetPropertyValue<T>(T record, string propertyName) where T : ISyncEntity
    {
        var type = typeof(T);
        var cacheKey = (type, propertyName);
        
        // Try to get cached property info
        if (!_propertyByNameCache.TryGetValue(cacheKey, out var property))
        {
            // Not cached, perform lookup
            property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            _propertyByNameCache[cacheKey] = property;
        }
        
        return property?.GetValue(record);
    }

    /// <summary>
    /// Converts an entity to a dictionary for temp table insertion.
    /// </summary>
    /// <remarks>
    /// <para>Excludes <c>IsDirty</c> (client-only tracking flag, has no server column) and
    /// <c>SyncSessionId</c> (assigned by the server during upsert, not sourced from the client).</para>
    /// <para>Includes <c>ModifiedAtUtc</c> — the client's actual edit timestamp must flow into the
    /// temp table so the server upsert carries it into the production table. The server no longer
    /// overwrites it with its own clock; <c>SessionRecord.CommittedAtUtc</c> is the authoritative
    /// server-side processing timestamp.</para>
    /// </remarks>
    /// <typeparam name="T">The entity type implementing <see cref="ISyncEntity"/>.</typeparam>
    /// <param name="entity">The entity instance to convert.</param>
    /// <returns>Dictionary with column names as keys and property values as values.</returns>
    public static Dictionary<string, object?> EntityToDictionary<T>(T entity) where T : ISyncEntity
    {
        var properties = GetCachedProperties<T>();
        var dict = new Dictionary<string, object?>();

        foreach (var prop in properties)
        {
            // Check for [SyncColumn(Ignore = true)]
            var syncColumn = prop.GetCustomAttribute<SyncColumnAttribute>();
            if (syncColumn?.Ignore == true)
                continue;

            // Exclude IsDirty (client-only) and SyncSessionId (server-assigned during upsert).
            // ModifiedAtUtc is intentionally included — see remarks.
            if (IsClientOnlyProperty(prop.Name) || prop.Name == nameof(ISyncInfrastructure.SyncSessionId))
                continue;

            var columnName = syncColumn?.ColumnName ?? prop.Name;
            dict[columnName] = prop.GetValue(entity);
        }
        
        return dict;
    }

    /// <summary>
    /// Converts a <see cref="Dictionary{String, Object}"/> to a strongly-typed entity instance.
    /// </summary>
    /// <remarks>
    /// Used for deserializing API responses from server to client. Handles
    /// <see cref="System.Text.Json.JsonElement"/> unwrapping, type conversions, and
    /// case-insensitive property matching. Delegates population to
    /// <see cref="PopulateEntity"/> to share logic with the non-generic overload.
    /// Property metadata is resolved from the per-type reflection cache — no reflection
    /// occurs on subsequent calls for the same type.
    /// </remarks>
    /// <typeparam name="T">The entity type implementing <see cref="ISyncEntity"/>.</typeparam>
    /// <param name="dict">Dictionary with property names as keys and raw values (including
    /// <see cref="System.Text.Json.JsonElement"/> boxed values) as values.</param>
    /// <returns>A fully populated instance of <typeparamref name="T"/>.</returns>
    public static T DictionaryToEntity<T>(Dictionary<string, object?> dict) where T : ISyncEntity
    {
        var entity = Activator.CreateInstance<T>();
        PopulateEntity(entity, GetCachedProperties<T>(), dict);
        return entity;
    }

    /// <summary>
    /// Converts a <see cref="Dictionary{String, Object}"/> to an entity instance whose type
    /// is only known at runtime.
    /// </summary>
    /// <remarks>
    /// Functionally identical to <see cref="DictionaryToEntity{T}"/> but accepts a
    /// <see cref="Type"/> argument instead of a generic type parameter. Used by
    /// <c>ClientDatabaseSeedWriter</c> and any other caller that resolves entity types
    /// dynamically (e.g., from <c>ITableMetadataCache</c>). Both overloads delegate to the
    /// same <see cref="PopulateEntity"/> helper and share the same reflection cache, so
    /// warming one overload warms the other for the same type.
    /// </remarks>
    /// <param name="dict">Dictionary with property names as keys and raw values (including
    /// <see cref="System.Text.Json.JsonElement"/> boxed values) as values.</param>
    /// <param name="entityType">The concrete entity <see cref="Type"/> to instantiate.
    /// Must implement <see cref="ISyncEntity"/> and have a public parameterless constructor.</param>
    /// <returns>A fully populated entity instance typed as <see cref="object"/>. Cast to the
    /// concrete type or to <see cref="ISyncEntity"/> as needed by the caller.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="entityType"/> cannot be instantiated (e.g., abstract class,
    /// no parameterless constructor).
    /// </exception>
    public static object DictionaryToEntity(Dictionary<string, object?> dict, Type entityType)
    {
        var entity = Activator.CreateInstance(entityType)
            ?? throw new InvalidOperationException(
                $"Cannot create instance of '{entityType.FullName}'. " +
                "Ensure the type has a public parameterless constructor and is not abstract.");
        PopulateEntity(entity, GetCachedProperties(entityType), dict);
        return entity;
    }

    /// <summary>
    /// Populates the writable properties of <paramref name="entity"/> from
    /// <paramref name="dict"/> using the pre-fetched <paramref name="properties"/> array.
    /// </summary>
    /// <remarks>
    /// This is the shared implementation body for both <see cref="DictionaryToEntity{T}"/>
    /// and <see cref="DictionaryToEntity(Dictionary{String, Object?}, Type)"/>. Keeping the
    /// logic here means any bug fix or behavioural change automatically applies to both
    /// overloads.
    ///
    /// Matching is case-insensitive: a dictionary key <c>"name"</c> will populate a property
    /// named <c>"Name"</c>. The case-insensitive lookup dictionary is built once per call —
    /// O(K) where K is the number of dictionary keys — rather than scanning the dictionary
    /// per property.
    ///
    /// <see cref="System.Text.Json.JsonElement"/> values are unwrapped to their CLR equivalents
    /// via <see cref="UnwrapJsonElement(System.Text.Json.JsonElement, Type)"/> before assignment.
    /// Type mismatches after unwrapping are handled by <c>ConvertValue</c>.
    /// </remarks>
    /// <param name="entity">The entity instance to populate. Must not be <c>null</c>.</param>
    /// <param name="properties">Cached <see cref="PropertyInfo"/> array for the entity type,
    /// obtained from <see cref="GetCachedProperties{T}"/> or
    /// <see cref="GetCachedProperties(Type)"/>.</param>
    /// <param name="dict">Source dictionary. Keys are matched case-insensitively against
    /// property names.</param>
    private static void PopulateEntity(
        object entity,
        PropertyInfo[] properties,
        Dictionary<string, object?> dict)
    {
        // Build case-insensitive lookup once — O(K) — instead of O(P×K) per-property scan
        var lookup = new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase);

        foreach (var prop in properties)
        {
            if (!lookup.TryGetValue(prop.Name, out var value) || value == null)
                continue;

            // Unwrap JsonElement if needed (common with System.Text.Json deserialization)
            if (value is System.Text.Json.JsonElement jsonElement)
            {
                value = UnwrapJsonElement(jsonElement, prop.PropertyType);
            }

            if (value == null)
            {
                prop.SetValue(entity, null);
                continue;
            }

            // Handle type conversions
            if (prop.PropertyType != value.GetType())
            {
                value = ConvertValue(value, prop.PropertyType);
            }

            prop.SetValue(entity, value);
        }
    }

    /// <summary>
    /// Unwraps a <see cref="JsonElement"/> to its natural CLR type without a type hint.
    /// </summary>
    /// <remarks>
    /// Use this overload when the target CLR type is unknown at the call site (e.g., generic
    /// dictionary unwrapping). For <c>Object</c> and <c>Array</c> kinds, returns the raw JSON
    /// string via <c>ToString()</c> rather than attempting deserialization.
    /// Use the typed overload (<see cref="UnwrapJsonElement(JsonElement, Type)"/>) when the
    /// property type is known to get a properly typed result.
    /// </remarks>
    /// <param name="element">The <see cref="JsonElement"/> to unwrap.</param>
    /// <returns>The CLR value, or <c>null</c> for <see cref="JsonValueKind.Null"/>.</returns>
    public static object? UnwrapJsonElement(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            // TryGetDateTimeOffset on every string was benchmarked (2026-02-22) against two
            // alternatives: digit-prefix guard and type-aware column lookup. Results across
            // BatchSize 1/100/1K/10K showed this strategy is fastest or co-fastest in all cases.
            // The digit-prefix guard adds GetString() allocation overhead; type-aware adds
            // HashSet + ToLowerInvariant() per key. Do not optimise without re-running benchmarks.
            if (element.TryGetDateTimeOffset(out var dto))
                return dto.UtcDateTime;

            return element.GetString();
        }

        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    /// <summary>
    /// Unwraps a <see cref="JsonElement"/> to the specified target type.
    /// </summary>
    /// <remarks>
    /// Use this overload when the property type is known (e.g., inside <see cref="DictionaryToEntity{T}"/>).
    /// Handles nullable types by unwrapping to the underlying type before conversion.
    /// For <c>Object</c> and <c>Array</c> kinds, delegates to
    /// <see cref="JsonSerializer.Deserialize(JsonElement, Type, JsonSerializerOptions?)"/>,
    /// which may throw if the JSON structure does not match <paramref name="targetType"/>.
    /// Use the untyped overload (<see cref="UnwrapJsonElement(JsonElement)"/>) when the target
    /// type is unknown and a raw string fallback is acceptable.
    /// </remarks>
    /// <param name="element">The <see cref="JsonElement"/> to unwrap.</param>
    /// <param name="targetType">The CLR type to convert to, including nullable types.</param>
    /// <returns>The typed CLR value, or <c>null</c> for <see cref="JsonValueKind.Null"/>.</returns>
    /// <exception cref="JsonException">
    /// Thrown when <paramref name="element"/> has kind <c>Object</c> or <c>Array</c> and
    /// <see cref="JsonSerializer.Deserialize(JsonElement, Type, JsonSerializerOptions?)"/> fails
    /// to deserialize to <paramref name="targetType"/>.
    /// </exception>
    public static object? UnwrapJsonElement(System.Text.Json.JsonElement element, Type targetType)
    {
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number when underlyingType == typeof(int) => element.GetInt32(),
            System.Text.Json.JsonValueKind.Number when underlyingType == typeof(long) => element.GetInt64(),
            System.Text.Json.JsonValueKind.Number when underlyingType == typeof(decimal) => element.GetDecimal(),
            System.Text.Json.JsonValueKind.Number when underlyingType == typeof(double) => element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => element.Deserialize(targetType)
        };
    }

    /// <summary>
    /// Unwraps all JsonElement values in a dictionary.
    /// Used after HTTP JSON deserialization to convert JsonElement objects to CLR types.
    /// </summary>
    public static Dictionary<string, object?> UnwrapJsonElements(Dictionary<string, object?> dict)
    {
        var result = new Dictionary<string, object?>();
        
        foreach (var kvp in dict)
        {
            result[kvp.Key] = kvp.Value is System.Text.Json.JsonElement element
                ? UnwrapJsonElement(element)
                : kvp.Value;
        }
        
        return result;
    }
    

    /// <summary>
    /// Converts a value to the target type.
    /// Handles nullable types and common conversions (DateTime, Guid, numeric types).
    /// </summary>
    private static object? ConvertValue(object value, Type targetType)
    {
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            if (underlyingType == typeof(DateTime) && value is string dateStr)
                return DateTime.Parse(dateStr);
            if (underlyingType == typeof(Guid) && value is string guidStr)
                return Guid.Parse(guidStr);
            if (underlyingType == typeof(int) && value is long longVal)
                return (int)longVal;

            return Convert.ChangeType(value, underlyingType);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Failed to convert value '{value}' (type: {value.GetType().Name}) " +
                $"to target type '{targetType.Name}'.", ex);
        }
    }

    /// <summary>
    /// Returns the cached <see cref="PropertyInfo"/> array for <typeparamref name="T"/>,
    /// performing reflection only on the first call for each type.
    /// </summary>
    /// <remarks>
    /// Keyed by <see cref="Type"/> in <c>_propertiesCache</c>. The same cache entry is
    /// shared with the non-generic overload <see cref="GetCachedProperties(Type)"/>, so
    /// warming this overload also warms the non-generic one for the same type and vice versa.
    /// </remarks>
    /// <typeparam name="T">The entity type implementing <see cref="ISyncEntity"/>.</typeparam>
    /// <returns>All public instance properties declared on <typeparamref name="T"/>.</returns>
    private static PropertyInfo[] GetCachedProperties<T>() where T : ISyncEntity
        => GetCachedProperties(typeof(T));

    /// <summary>
    /// Returns the cached <see cref="PropertyInfo"/> array for <paramref name="type"/>,
    /// performing reflection only on the first call for each type.
    /// </summary>
    /// <remarks>
    /// Non-generic companion to <see cref="GetCachedProperties{T}"/>. Both overloads write
    /// to and read from the same <c>_propertiesCache</c> keyed by <see cref="Type"/>,
    /// so there is no duplicate reflection cost when a type has been accessed via either
    /// overload. Called by <see cref="DictionaryToEntity(Dictionary{String, Object?}, Type)"/>
    /// and <see cref="PopulateEntity"/> when the entity type is only known at runtime.
    /// </remarks>
    /// <param name="type">The entity type to inspect.</param>
    /// <returns>All public instance properties declared on <paramref name="type"/>.</returns>
    private static PropertyInfo[] GetCachedProperties(Type type)
    {
        if (_propertiesCache.TryGetValue(type, out var cachedProperties))
            return cachedProperties;

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _propertiesCache[type] = properties;
        return properties;
    }
}
