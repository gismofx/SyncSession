using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using MySqlConnector;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using Xunit;

namespace SyncSession.IntegrationTests.Schema;

/// <summary>
/// Detects drift between TestSchemaBuilder.InfrastructureTableColumns and 001_Infrastructure.sql.
/// 
/// 001_Infrastructure.sql is the authority. If either test fails, update TestSchemaBuilder
/// to match the SQL file — never the other way around.
/// </summary>
[Collection("MariaDB Collection")]
public class SchemaDriftTests
{
    private readonly TestDatabaseFactory _dbFactory;

    public SchemaDriftTests(MariaDbFixture fixture)
    {
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    private async Task<Dictionary<string, HashSet<string>>> GetMySqlColumnsAsync(string testName)
    {
        var connectionString = await _dbFactory.CreateDatabaseAsync(testName);
        var dbName = new MySqlConnectionStringBuilder(connectionString).Database;

        await using var conn = new MySqlConnection(connectionString);

        var result = new Dictionary<string, HashSet<string>>();

        foreach (var tableName in TestSchemaBuilder.InfrastructureTableColumns.Keys)
        {
            var columns = (await conn.QueryAsync<string>(
                @"SELECT COLUMN_NAME
                  FROM INFORMATION_SCHEMA.COLUMNS
                  WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
                  ORDER BY ORDINAL_POSITION",
                new { db = dbName, table = tableName })).ToHashSet();

            result[tableName] = columns;
        }

        return result;
    }

    [Fact]
    public async Task SchemaDrift_NothingIn001_MissingFromTestSchemaBuilder()
    {
        // Columns in MySQL (001_Infrastructure.sql) that TestSchemaBuilder doesn't declare.
        // Fix: add the missing columns to TestSchemaBuilder.InfrastructureTableColumns.
        var mysqlColumns = await GetMySqlColumnsAsync(
            nameof(SchemaDrift_NothingIn001_MissingFromTestSchemaBuilder));

        foreach (var (table, sqlColumns) in mysqlColumns)
        {
            var builderColumns = TestSchemaBuilder.InfrastructureTableColumns[table];
            var missing = sqlColumns.Except(builderColumns).ToList();

            missing.Should().BeEmpty(
                $"TestSchemaBuilder is missing columns for '{table}' " +
                $"that exist in 001_Infrastructure.sql: [{string.Join(", ", missing)}]. " +
                $"Update TestSchemaBuilder.InfrastructureTableColumns.");
        }
    }

    [Fact]
    public async Task SchemaDrift_NothingInTestSchemaBuilder_MissingFrom001()
    {
        // Columns declared in TestSchemaBuilder that don't exist in MySQL (001_Infrastructure.sql).
        // Fix: add the missing columns to 001_Infrastructure.sql, or remove from TestSchemaBuilder.
        var mysqlColumns = await GetMySqlColumnsAsync(
            nameof(SchemaDrift_NothingInTestSchemaBuilder_MissingFrom001));

        foreach (var (table, builderColumns) in TestSchemaBuilder.InfrastructureTableColumns)
        {
            var sqlColumns = mysqlColumns[table];
            var missing = builderColumns.Except(sqlColumns).ToList();

            missing.Should().BeEmpty(
                $"001_Infrastructure.sql is missing columns for '{table}' " +
                $"that are declared in TestSchemaBuilder: [{string.Join(", ", missing)}]. " +
                $"Update 001_Infrastructure.sql or remove from TestSchemaBuilder.");
        }
    }
}
