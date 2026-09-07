using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Core.Models;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Server.Database;
using Xunit;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// Integration tests for <see cref="MySqlServerDatabase.EnsureSyncIndexesAsync"/>.
/// </summary>
/// <remarks>
/// These carry the whole proof for Session 41b: the matching rules live inside a method that needs a
/// live connection, so there is nowhere else they can be exercised.
/// <para>
/// The default test configuration registers Customers and Orders (multi-tenant) and OrderItems
/// (not), and <c>002_ExampleBusinessTables.sql</c> gives each the production index shape —
/// single-column <c>IX_&lt;T&gt;_Session</c> and, for the multi-tenant pair, <c>IX_&lt;T&gt;_Tenant</c>.
/// That is deliberately the same starting point production was in.
/// </para>
/// </remarks>
[Collection("MariaDB Collection")]
public class EnsureSyncIndexesTests
{
    private const string CustomersComposite = "IX_Customers_Session_Tenant";
    private const string OrdersComposite = "IX_Orders_Session_Tenant";
    private const string OrderItemsSingle = "IX_OrderItems_Session";

    private readonly TestDatabaseFactory _dbFactory;

    public EnsureSyncIndexesTests(MariaDbFixture fixture)
    {
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    #region Helper Methods

    private static MySqlServerDatabase CreateServerDb(string connectionString)
    {
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();

        if (!SyncSession.Core.Utilities.EntityReflectionHelper.IsInitialized)
            SyncSession.Core.Utilities.EntityReflectionHelper.Initialize(config);

        var cache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        return new MySqlServerDatabase(connectionString, cache, config, NullLogger<MySqlServerDatabase>.Instance);
    }

    /// <summary>
    /// Every index on a table as its ordered column list, keyed by index name.
    /// </summary>
    /// <remarks>
    /// Ordered by SEQ_IN_INDEX, because column *order* is the whole question here — a set-based
    /// helper would make the wrong-order test pass for the wrong reason.
    /// </remarks>
    private static async Task<Dictionary<string, List<string>>> GetIndexesAsync(
        string connectionString, string tableName)
    {
        using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT INDEX_NAME AS IndexName, COLUMN_NAME AS ColumnName
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName
            ORDER BY INDEX_NAME, SEQ_IN_INDEX",
            new { TableName = tableName });

        var byIndex = new Dictionary<string, List<string>>();
        foreach (var row in rows)
        {
            var name = (string)row.IndexName;
            if (!byIndex.TryGetValue(name, out var columns))
            {
                columns = new List<string>();
                byIndex[name] = columns;
            }
            columns.Add((string)row.ColumnName);
        }

        return byIndex;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql);
    }

    private static SyncIndexAction ActionFor(IReadOnlyList<SyncIndexAction> actions, string table) =>
        actions.Single(a => a.TableName == table);

    #endregion

    #region Creation

    [Fact]
    public async Task EnsureSyncIndexes_CreatesComposite_ForMultiTenantTable()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_CreatesComposite_ForMultiTenantTable));
        var db = CreateServerDb(connStr);

        var actions = await db.EnsureSyncIndexesAsync();

        ActionFor(actions, "Customers").Outcome.Should().Be(SyncIndexOutcome.Created);
        var indexes = await GetIndexesAsync(connStr, "Customers");
        indexes.Should().ContainKey(CustomersComposite);
        indexes[CustomersComposite].Should().Equal("SyncSessionId", "TenantId");
    }

    [Fact]
    public async Task EnsureSyncIndexes_CreatesSingleColumn_ForNonMultiTenantTable()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_CreatesSingleColumn_ForNonMultiTenantTable));

        // The sample schema already ships IX_OrderItems_Session, which would satisfy the requirement
        // outright. Drop it so this test observes a creation rather than a no-op wearing its clothes.
        await ExecuteAsync(connStr, $"DROP INDEX `{OrderItemsSingle}` ON `OrderItems`");

        var db = CreateServerDb(connStr);
        var actions = await db.EnsureSyncIndexesAsync();

        ActionFor(actions, "OrderItems").Outcome.Should().Be(SyncIndexOutcome.Created);
        var indexes = await GetIndexesAsync(connStr, "OrderItems");
        indexes.Should().ContainKey(OrderItemsSingle);
        indexes[OrderItemsSingle].Should().Equal("SyncSessionId");
    }

    [Fact]
    public async Task EnsureSyncIndexes_IsIdempotent_OnSecondRun()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_IsIdempotent_OnSecondRun));
        var db = CreateServerDb(connStr);

        await db.EnsureSyncIndexesAsync();
        var before = await GetIndexesAsync(connStr, "Customers");

        var second = await db.EnsureSyncIndexesAsync();

        second.Should().OnlyContain(a => a.Outcome == SyncIndexOutcome.AlreadyPresent);
        (await GetIndexesAsync(connStr, "Customers")).Keys.Should().BeEquivalentTo(before.Keys);
    }

    #endregion

    #region Matching Rules

    [Fact]
    public async Task EnsureSyncIndexes_AcceptsPrefixMatch_UnderAnyName()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_AcceptsPrefixMatch_UnderAnyName));

        // An operator's own index, wrong name, extra trailing column — still gives the optimiser
        // exactly what the pull predicate needs.
        await ExecuteAsync(connStr,
            "CREATE INDEX `operators_own_index` ON `Customers` (`SyncSessionId`, `TenantId`, `Email`)");

        var db = CreateServerDb(connStr);
        var actions = await db.EnsureSyncIndexesAsync();

        var customers = ActionFor(actions, "Customers");
        customers.Outcome.Should().Be(SyncIndexOutcome.AlreadyPresent);
        customers.Detail.Should().Contain("operators_own_index");
        (await GetIndexesAsync(connStr, "Customers")).Should().NotContainKey(CustomersComposite);
    }

    [Fact]
    public async Task EnsureSyncIndexes_RejectsWrongColumnOrder()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_RejectsWrongColumnOrder));

        // (TenantId, SyncSessionId) holds the same two columns and is NOT the index this needs —
        // the decision of 2026-09-03 requires SyncSessionId to lead.
        await ExecuteAsync(connStr,
            "CREATE INDEX `wrong_order` ON `Customers` (`TenantId`, `SyncSessionId`)");

        var db = CreateServerDb(connStr);
        var actions = await db.EnsureSyncIndexesAsync();

        ActionFor(actions, "Customers").Outcome.Should().Be(SyncIndexOutcome.Created);
        var indexes = await GetIndexesAsync(connStr, "Customers");
        indexes[CustomersComposite].Should().Equal("SyncSessionId", "TenantId");
    }

    [Fact]
    public async Task EnsureSyncIndexes_RejectsSingleColumnIndex_OnMultiTenantTable()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_RejectsSingleColumnIndex_OnMultiTenantTable));

        // IX_Customers_Session (SyncSessionId) ships with the sample schema and is exactly what
        // production had. It must not be mistaken for sufficient.
        var before = await GetIndexesAsync(connStr, "Customers");
        before.Should().ContainKey("IX_Customers_Session");

        var db = CreateServerDb(connStr);
        var actions = await db.EnsureSyncIndexesAsync();

        ActionFor(actions, "Orders").Outcome.Should().Be(SyncIndexOutcome.Created);
        (await GetIndexesAsync(connStr, "Orders")).Should().ContainKey(OrdersComposite);
    }

    #endregion

    #region Safety

    [Fact]
    public async Task EnsureSyncIndexes_CreatesNothing_WhenCreateMissingIsFalse()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_CreatesNothing_WhenCreateMissingIsFalse));
        var before = await GetIndexesAsync(connStr, "Customers");

        var db = CreateServerDb(connStr);
        var actions = await db.EnsureSyncIndexesAsync(createMissing: false);

        ActionFor(actions, "Customers").Outcome.Should().Be(SyncIndexOutcome.WouldCreate);
        (await GetIndexesAsync(connStr, "Customers")).Keys.Should().BeEquivalentTo(before.Keys);
    }

    [Fact]
    public async Task EnsureSyncIndexes_SkipsMissingTable_AndKeepsGoing()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_SkipsMissingTable_AndKeepsGoing));

        // A registered entity table can be absent on a partially-provisioned deployment. One such
        // table must not cost the others their index.
        await ExecuteAsync(connStr, "DROP TABLE `OrderItems`");

        var db = CreateServerDb(connStr);
        var actions = await db.EnsureSyncIndexesAsync();

        ActionFor(actions, "OrderItems").Outcome.Should().Be(SyncIndexOutcome.Skipped);
        ActionFor(actions, "Customers").Outcome.Should().Be(SyncIndexOutcome.Created);
        (await GetIndexesAsync(connStr, "Customers")).Should().ContainKey(CustomersComposite);
    }

    [Fact]
    public async Task EnsureSyncIndexes_DropsNothing()
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(
            nameof(EnsureSyncIndexes_DropsNothing));

        var before = new Dictionary<string, List<string>>();
        foreach (var table in new[] { "Customers", "Orders", "OrderItems" })
            foreach (var ix in await GetIndexesAsync(connStr, table))
                before[$"{table}.{ix.Key}"] = ix.Value;

        var db = CreateServerDb(connStr);
        await db.EnsureSyncIndexesAsync();

        var after = new Dictionary<string, List<string>>();
        foreach (var table in new[] { "Customers", "Orders", "OrderItems" })
            foreach (var ix in await GetIndexesAsync(connStr, table))
                after[$"{table}.{ix.Key}"] = ix.Value;

        // Every index that was there is still there, with its columns unchanged. The composite makes
        // IX_<T>_Session redundant; redundant is not the library's cue to remove it.
        after.Keys.Should().Contain(before.Keys);
        foreach (var kvp in before)
            after[kvp.Key].Should().Equal(kvp.Value);
    }

    #endregion
}
