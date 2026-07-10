using System.Collections.Generic;

namespace SyncSession.Client.Database;

/// <summary>
/// SQL DDL for the library's client-side bookkeeping table, shared by all SQLite-backed
/// <see cref="SyncSession.Core.Interfaces.IClientDatabase"/> implementations
/// (<see cref="SqliteClientDatabase"/>, custom WASM/IndexedDB-backed stores, etc.).
/// </summary>
/// <remarks>
/// The statement is <c>CREATE TABLE IF NOT EXISTS</c> — idempotent and safe to run on every
/// application startup. Treat this as the single source of truth so the bookkeeping
/// schema cannot drift between implementations; execute them via whatever connection your store
/// already uses (Microsoft.Data.Sqlite, wa-sqlite, etc.).
/// </remarks>
public static class SqliteClientSchema
{
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
        LocalSyncMetadataDdl
    };
}