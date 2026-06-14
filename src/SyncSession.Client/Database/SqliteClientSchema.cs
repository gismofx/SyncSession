using System.Collections.Generic;

namespace SyncSession.Client.Database;

/// <summary>
/// SQL DDL for the library's client-side bookkeeping tables, shared by all SQLite-backed
/// <see cref="SyncSession.Core.Interfaces.IClientDatabase"/> implementations
/// (<see cref="SqliteClientDatabase"/>, custom WASM/IndexedDB-backed stores, etc.).
/// </summary>
/// <remarks>
/// Every statement is <c>CREATE TABLE IF NOT EXISTS</c> — idempotent and safe to run on every
/// application startup. Treat these constants as the single source of truth so the bookkeeping
/// schema cannot drift between implementations; execute them via whatever connection your store
/// already uses (Microsoft.Data.Sqlite, wa-sqlite, etc.).
/// </remarks>
public static class SqliteClientSchema
{
    /// <summary>
    /// Per-table last-synced version bookkeeping. Backs <c>GetLastSyncVersionAsync</c> /
    /// <c>UpdateLastSyncVersionAsync</c>.
    /// </summary>
    public const string LocalSyncStateDdl = @"
        CREATE TABLE IF NOT EXISTS LocalSyncState (
            TableName TEXT PRIMARY KEY,
            LastSyncVersion INTEGER NOT NULL DEFAULT 0,
            LastSyncCompletedAtUtc TEXT,
            CreatedAtUtc TEXT NOT NULL DEFAULT (datetime('now')),
            UpdatedAtUtc TEXT NOT NULL DEFAULT (datetime('now'))
        )";

    /// <summary>
    /// Client key/value store. Backs <c>GetClientMetadataAsync</c> / <c>SetClientMetadataAsync</c>
    /// and holds the persisted tenant binding. <c>Key</c> is case-sensitive.
    /// </summary>
    public const string LocalSyncMetadataDdl = @"
        CREATE TABLE IF NOT EXISTS LocalSyncMetadata (
            Key          TEXT NOT NULL PRIMARY KEY,
            Value        TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL DEFAULT (datetime('now'))
        )";

    /// <summary>
    /// All bookkeeping-table DDL statements, in creation order. Execute each once at startup.
    /// </summary>
    public static IReadOnlyList<string> AllStatements { get; } = new[]
    {
        LocalSyncStateDdl,
        LocalSyncMetadataDdl
    };
}