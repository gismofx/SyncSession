using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace SyncSession.IntegrationTests.Infrastructure;

/// <summary>
/// Single source of truth for infrastructure schema in SQLite-compatible syntax.
/// Used by SQLite-based test fixtures (CleanupTests, etc.) to create infrastructure tables.
/// Validated against 001_Infrastructure.sql by SchemaDriftTests — do not edit in isolation.
/// </summary>
public static class TestSchemaBuilder
{
    /// <summary>
    /// Canonical column names per infrastructure table.
    /// Must stay in sync with 001_Infrastructure.sql — SchemaDriftTests enforces this.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> InfrastructureTableColumns =
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["SessionRecords"] = new HashSet<string>
            {
                "SessionId", "TenantId", "DeviceId", "UserId", "UserDisplayName",
                "SessionType", "Status", "SyncVersion",
                "CreatedAtUtc", "LastActivityUtc", "CommittedAtUtc",
                "ErrorMessage", "TotalRows", "RowCountsJson"
            },
            ["SyncSessionTables"] = new HashSet<string>
            {
                "Id", "SessionId", "TableName", "TempTableName",
                "ProcessingPriority", "UsesSharedTable", "Status",
                "EstimatedRecordCount", "ActualRecordCount", "CreatedAtUtc"
            },
            ["ClientProcessedSessions"] = new HashSet<string>
            {
                "DeviceId", "SessionId", "ProcessedAtUtc"
            },
            ["SeedSnapshots"] = new HashSet<string>
            {
                "SeedId", "DeviceId", "TenantId", "Status",
                "CreatedAtUtc", "LastActivityUtc"
            }
        };

    /// <summary>
    /// Creates all infrastructure tables in the given SQLite connection.
    /// SQLite-compatible equivalent of 001_Infrastructure.sql.
    /// </summary>
    public static async Task BuildAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(@"
            CREATE TABLE SessionRecords (
                SessionId TEXT PRIMARY KEY,
                TenantId TEXT NULL,
                DeviceId TEXT NULL,
                UserId TEXT NULL,
                UserDisplayName TEXT NULL,
                SessionType TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Staging',
                SyncVersion INTEGER NULL,
                CreatedAtUtc TEXT NOT NULL,
                LastActivityUtc TEXT NOT NULL,
                CommittedAtUtc TEXT NULL,
                ErrorMessage TEXT NULL,
                TotalRows INTEGER NOT NULL DEFAULT 0,
                RowCountsJson TEXT NULL
            )");

        await connection.ExecuteAsync(@"
            CREATE TABLE SyncSessionTables (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                TableName TEXT NOT NULL,
                TempTableName TEXT NULL,
                ProcessingPriority INTEGER NOT NULL,
                UsesSharedTable INTEGER NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Staging',
                EstimatedRecordCount INTEGER NULL,
                ActualRecordCount INTEGER NULL,
                CreatedAtUtc TEXT NOT NULL DEFAULT (datetime('now'))
            )");

        await connection.ExecuteAsync(@"
            CREATE TABLE ClientProcessedSessions (
                DeviceId TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                ProcessedAtUtc TEXT NOT NULL,
                PRIMARY KEY (DeviceId, SessionId)
            )");

        await connection.ExecuteAsync(@"
            CREATE TABLE SeedSnapshots (
                SeedId TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                TenantId TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Active',
                CreatedAtUtc TEXT NOT NULL,
                LastActivityUtc TEXT NOT NULL,
                PRIMARY KEY (SeedId),
                UNIQUE (DeviceId, TenantId)
            )");
    }
}
