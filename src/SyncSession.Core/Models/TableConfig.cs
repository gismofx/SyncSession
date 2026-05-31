using System;
using SyncSession.Core.Interfaces;

namespace SyncSession.Core.Models;

/// <summary>
/// Configuration for a syncable table.
/// Combines entity metadata, sync settings, and runtime handler.
/// </summary>
public class TableConfig
{
    /// <summary>
    /// The entity type (e.g., <c>typeof(Customer)</c>). Must implement <see cref="ISyncEntity"/>.
    /// </summary>
    public Type EntityType { get; init; } = null!;
    
    /// <summary>
    /// Name of the database table.
    /// </summary>
    public string TableName { get; init; } = string.Empty;
    
    /// <summary>
    /// Processing priority (lower values are processed first).
    /// Ensures parent tables are synchronized before child tables.
    /// </summary>
    public int Priority { get; init; }
    
    /// <summary>
    /// Whether this table is enabled for synchronization.
    /// </summary>
    public bool Enabled { get; init; } = true;
    
    /// <summary>
    /// Custom batch size override for this table (<c>null</c> to use global default).
    /// </summary>
    public int? CustomBatchSize { get; init; }
    
    /// <summary>
    /// Custom shared table threshold override for this table (<c>null</c> to use global default).
    /// </summary>
    public int? CustomSharedTableThreshold { get; init; }
    
    /// <summary>
    /// When true, this table is available for seeding only — not push/pull sync.
    /// Use for legacy entities that have sync columns in the DB but don't implement ISyncEntity.
    /// </summary>
    public bool SeedOnly { get; init; }

    /// <summary>
    /// When true, snapshot creation filters rows by <c>TenantId</c>.
    /// Set this for any table whose data is tenant-scoped but does not implement
    /// <see cref="IMultiTenantSyncEntity"/> (e.g. legacy seed-only tables).
    /// </summary>
    public bool TenantFiltered { get; init; }

    /// <summary>
    /// Strongly-typed handler for this table's sync operations.
    /// Eliminates reflection and provides type-safe push/pull methods.
    /// Set by <see cref="ClientSyncEngineBuilder"/> during engine construction.
    /// </summary>
    public ITableSyncHandler? Handler { get; set; }
}
