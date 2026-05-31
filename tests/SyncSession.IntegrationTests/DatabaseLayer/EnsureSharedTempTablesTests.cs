using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Core.Services;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Server.Database;
using Xunit;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// Integration tests for <see cref="MySqlServerDatabase.EnsureSharedTempTablesAsync"/>.
/// Validates automatic creation and schema drift correction of shared temp tables.
/// </summary>
[Collection("MariaDB Collection")]
public class EnsureSharedTempTablesTests
{
    private readonly MariaDbFixture _fixture;
    private readonly TestDatabaseFactory _dbFactory;

    public EnsureSharedTempTablesTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    #region Helper Methods

    /// <summary>
    /// Creates a MySqlServerDatabase with the default test configuration.
    /// </summary>
    private static MySqlServerDatabase CreateServerDb(string connectionString)
    {
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();

        // EntityReflectionHelper must be initialized for TableMetadataCache
        if (!SyncSession.Core.Utilities.EntityReflectionHelper.IsInitialized)
            SyncSession.Core.Utilities.EntityReflectionHelper.Initialize(config);

        var cache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        return new MySqlServerDatabase(connectionString, cache, config, NullLogger<MySqlServerDatabase>.Instance);
    }

    /// <summary>
    /// Returns column metadata (name, type, nullable) for a table from INFORMATION_SCHEMA.
    /// </summary>
    private static async Task<List<(string Name, string Type, bool IsNullable)>> GetColumnInfoAsync(
        string connectionString, string tableName)
    {
        using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName
            ORDER BY ORDINAL_POSITION",
            new { TableName = tableName });

        return rows.Select(r => (
            Name: (string)r.COLUMN_NAME,
            Type: (string)r.COLUMN_TYPE,
            IsNullable: ((string)r.IS_NULLABLE) == "YES"
        )).ToList();
    }

    /// <summary>
    /// Drops all shared temp tables (TempPush* / TempPull*) so we can test creation from scratch.
    /// </summary>
    private static async Task DropSharedTempTablesAsync(string connectionString, params string[] tableNames)
    {
        using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var name in tableNames)
            await conn.ExecuteAsync($"DROP TABLE IF EXISTS `{name}`");
    }

    /// <summary>
    /// Checks whether a table exists in the current database.
    /// </summary>
    private static async Task<bool> TableExistsAsync(string connectionString, string tableName)
    {
        using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName",
            new { TableName = tableName });
        return count > 0;
    }

    #endregion

    #region Creation Tests

    [Fact]
    public async Task EnsureSharedTempTables_CreatesAllTempTables_WhenNoneExist()
    {
        // Arrange — database has entity tables but no temp tables
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSharedTempTables_CreatesAllTempTables_WhenNoneExist));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);

        // Act
        await db.EnsureSharedTempTablesAsync();

        // Assert — all 6 temp tables exist (3 entities × push + pull)
        (await TableExistsAsync(connStr, "TempPushCustomers")).Should().BeTrue();
        (await TableExistsAsync(connStr, "TempPullCustomers")).Should().BeTrue();
        (await TableExistsAsync(connStr, "TempPushOrders")).Should().BeTrue();
        (await TableExistsAsync(connStr, "TempPullOrders")).Should().BeTrue();
        (await TableExistsAsync(connStr, "TempPushOrderItems")).Should().BeTrue();
        (await TableExistsAsync(connStr, "TempPullOrderItems")).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureSharedTempTables_IsIdempotent_WhenTablesAlreadyExist()
    {
        // Arrange — run once to create, then run again
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSharedTempTables_IsIdempotent_WhenTablesAlreadyExist));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);
        await db.EnsureSharedTempTablesAsync(); // first run

        // Act — second run should be no-op
        var act = () => db.EnsureSharedTempTablesAsync();

        // Assert — no exceptions, tables still exist
        await act.Should().NotThrowAsync();
        (await TableExistsAsync(connStr, "TempPushCustomers")).Should().BeTrue();
        (await TableExistsAsync(connStr, "TempPullCustomers")).Should().BeTrue();
    }

    #endregion

    #region Push Table Structure Tests

    [Fact]
    public async Task PushTempTable_HasSequenceNumberAndSessionId()
    {
        // Arrange
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(PushTempTable_HasSequenceNumberAndSessionId));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);

        // Act
        await db.EnsureSharedTempTablesAsync();

        // Assert
        var cols = await GetColumnInfoAsync(connStr, "TempPushCustomers");
        var colNames = cols.Select(c => c.Name).ToList();

        colNames.Should().Contain("SequenceNumber");
        colNames.Should().Contain("SessionId");
    }

    [Fact]
    public async Task PushTempTable_ModifiedAtUtcIsNullable()
    {
        // Arrange
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(PushTempTable_ModifiedAtUtcIsNullable));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);

        // Act
        await db.EnsureSharedTempTablesAsync();

        // Assert — push tables relax ModifiedAtUtc nullability for client-omitted values
        var cols = await GetColumnInfoAsync(connStr, "TempPushCustomers");
        var modifiedCol = cols.Single(c => c.Name == "ModifiedAtUtc");
        modifiedCol.IsNullable.Should().BeTrue(
            "push temp table ModifiedAtUtc must be nullable — client may omit it; " +
            "UpsertFromTempTableAsync uses COALESCE to fill server time");
    }

    [Fact]
    public async Task PushTempTable_ExcludesSyncSessionId()
    {
        // Arrange
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(PushTempTable_ExcludesSyncSessionId));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);

        // Act
        await db.EnsureSharedTempTablesAsync();

        // Assert — SyncSessionId is server-assigned during upsert, not in push staging
        var cols = await GetColumnInfoAsync(connStr, "TempPushCustomers");
        cols.Select(c => c.Name).Should().NotContain("SyncSessionId");
    }

    #endregion

    #region Pull Table Structure Tests

    [Fact]
    public async Task PullTempTable_HasSessionIdButNoSequenceNumber()
    {
        // Arrange
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(PullTempTable_HasSessionIdButNoSequenceNumber));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);

        // Act
        await db.EnsureSharedTempTablesAsync();

        // Assert
        var cols = await GetColumnInfoAsync(connStr, "TempPullCustomers");
        var colNames = cols.Select(c => c.Name).ToList();

        colNames.Should().Contain("SessionId");
        colNames.Should().NotContain("SequenceNumber",
            "pull tables don't need ordering — they use PK (SessionId, Id)");
    }

    [Fact]
    public async Task PullTempTable_ContainsServerSelectColumns()
    {
        // Arrange
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(PullTempTable_ContainsServerSelectColumns));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);

        // Act
        await db.EnsureSharedTempTablesAsync();

        // Assert — pull tables should have all columns from GetColumnsForServerSelect
        var cols = await GetColumnInfoAsync(connStr, "TempPullCustomers");
        var colNames = cols.Select(c => c.Name).ToList();

        // Business columns + ModifiedAtUtc + IsDeleted + ModifiedByUserId
        colNames.Should().Contain("Id");
        colNames.Should().Contain("Name");
        colNames.Should().Contain("ModifiedAtUtc");
        colNames.Should().Contain("IsDeleted");
        colNames.Should().Contain("ModifiedByUserId");
    }

    #endregion

    #region Schema Drift Tests

    [Fact]
    public async Task EnsureSharedTempTables_AddsColumn_WhenTempTableIsMissingMetadataCacheColumn()
    {
        // This tests the schema drift correction mechanism. The real-world trigger is:
        //   1. Developer adds a property to the C# entity
        //   2. Runs a migration to add the column to the entity table
        //   3. On next startup, metadata cache includes the new column (from reflection)
        //   4. EnsureSharedTempTablesAsync detects the temp table is missing it → ALTER TABLE ADD
        //
        // We simulate this by dropping a column from an existing temp table. The code path
        // is identical — it compares metadata cache requirements against INFORMATION_SCHEMA
        // and doesn't distinguish between "was never there" and "was dropped".
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSharedTempTables_AddsColumn_WhenTempTableIsMissingMetadataCacheColumn));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);
        await db.EnsureSharedTempTablesAsync(); // initial creation

        // Drop a column the metadata cache expects
        using (var conn = new MySqlConnection(connStr))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("ALTER TABLE TempPushCustomers DROP COLUMN Email");
        }

        // Verify Email is gone
        var colsBefore = await GetColumnInfoAsync(connStr, "TempPushCustomers");
        colsBefore.Select(c => c.Name).Should().NotContain("Email");

        // Act — re-run should detect missing column and add it
        await db.EnsureSharedTempTablesAsync();

        // Assert — Email restored with correct type from entity table
        var colsAfter = await GetColumnInfoAsync(connStr, "TempPushCustomers");
        colsAfter.Select(c => c.Name).Should().Contain("Email",
            "Email column should be re-added by schema drift correction");

        // Verify type matches entity table
        var restoredCol = colsAfter.Single(c => c.Name == "Email");
        var entityCols = await GetColumnInfoAsync(connStr, "Customers");
        var entityEmailCol = entityCols.Single(c => c.Name == "Email");
        restoredCol.Type.Should().Be(entityEmailCol.Type,
            "restored column type should match the entity table");
    }

    [Fact]
    public async Task EnsureSharedTempTables_AddsColumn_ToPullTable_WhenMissing()
    {
        // Same mechanism as push test above — see comment there for why DROP simulates
        // the real-world "new column added to entity" scenario.
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSharedTempTables_AddsColumn_ToPullTable_WhenMissing));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);
        await db.EnsureSharedTempTablesAsync(); // initial creation

        // Drop a column from pull temp table
        using (var conn = new MySqlConnection(connStr))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("ALTER TABLE TempPullOrders DROP COLUMN Status");
        }

        // Act
        await db.EnsureSharedTempTablesAsync();

        // Assert — Status restored with correct type from entity table
        var cols = await GetColumnInfoAsync(connStr, "TempPullOrders");
        cols.Select(c => c.Name).Should().Contain("Status",
            "Status column should be re-added by schema drift correction");

        var restoredCol = cols.Single(c => c.Name == "Status");
        var entityCols = await GetColumnInfoAsync(connStr, "Orders");
        var entityStatusCol = entityCols.Single(c => c.Name == "Status");
        restoredCol.Type.Should().Be(entityStatusCol.Type,
            "restored column type should match the entity table");
    }

    #endregion

    #region Column Type Consistency Tests

    [Fact]
    public async Task TempTable_ColumnTypes_MatchEntityTable()
    {
        // Arrange
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTable_ColumnTypes_MatchEntityTable));

        await DropSharedTempTablesAsync(connStr,
            "TempPushCustomers", "TempPullCustomers",
            "TempPushOrders",    "TempPullOrders",
            "TempPushOrderItems","TempPullOrderItems");

        var db = CreateServerDb(connStr);

        // Act
        await db.EnsureSharedTempTablesAsync();

        // Assert — compare shared column types between entity and temp tables
        var entityCols = await GetColumnInfoAsync(connStr, "Customers");
        var pushCols = await GetColumnInfoAsync(connStr, "TempPushCustomers");
        var pullCols = await GetColumnInfoAsync(connStr, "TempPullCustomers");

        var entityLookup = entityCols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        // For each column in push table (excluding SessionId, SequenceNumber), type should match entity
        foreach (var col in pushCols.Where(c =>
            c.Name != "SessionId" && c.Name != "SequenceNumber"))
        {
            if (entityLookup.TryGetValue(col.Name, out var entityCol))
            {
                col.Type.Should().Be(entityCol.Type,
                    $"TempPush column '{col.Name}' type should match entity table");
            }
        }

        // Same for pull table
        foreach (var col in pullCols.Where(c => c.Name != "SessionId"))
        {
            if (entityLookup.TryGetValue(col.Name, out var entityCol))
            {
                col.Type.Should().Be(entityCol.Type,
                    $"TempPull column '{col.Name}' type should match entity table");
            }
        }
    }

    #endregion
}
