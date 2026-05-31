using MySqlConnector;
using SyncSession.Tools.Models;

namespace SyncSession.Tools.Assessment;

/// <summary>
/// Queries INFORMATION_SCHEMA to inspect table and column structure.
/// </summary>
public class SchemaInspector
{
    private static readonly string[] SyncColumns =
        ["IsDeleted", "ModifiedByUserId", "ModifiedAtUtc", "SyncSessionId"];

    private static readonly string[] TenantColumnPatterns =
        ["tenantid", "clientid", "tenant_id", "client_id"];

    private readonly string _connectionString;

    public SchemaInspector(string connectionString) =>
        _connectionString = connectionString;

    public async Task<List<string>> GetAllTableNamesAsync()
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        var db = conn.Database;
        var tables = new List<string>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME
            """;
        cmd.Parameters.AddWithValue("@db", db);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        return tables;
    }

    public async Task<TableAssessment> AssessTableAsync(string tableName)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        var db = conn.Database;

        var assessment = new TableAssessment { TableName = tableName, ExistsInDatabase = true };

        // Load column info
        var columns = new List<(string Name, string DataType, string ColumnKey)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE, COLUMN_KEY
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("@db", db);
        cmd.Parameters.AddWithValue("@table", tableName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        // Populate existing columns
        assessment.ExistingColumns = columns.Select(c => c.Name).ToList();

        // Check sync columns
        var columnNames = assessment.ExistingColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        assessment.MissingSyncColumns = SyncColumns
            .Where(c => !columnNames.Contains(c))
            .ToList();

        foreach (var missing in assessment.MissingSyncColumns)
            assessment.Checks.Add(new CheckResult(CheckStatus.Fail,
                $"Missing required sync column: {missing}"));

        if (assessment.MissingSyncColumns.Count == 0)
            assessment.Checks.Add(new CheckResult(CheckStatus.Pass, "All sync columns present"));

        // Check PK type
        var pk = columns.FirstOrDefault(c => c.ColumnKey == "PRI");
        if (pk != default)
        {
            assessment.PrimaryKeyType = pk.DataType;
            assessment.PrimaryKeyIsUuid = pk.DataType.Contains("char", StringComparison.OrdinalIgnoreCase)
                                       || pk.DataType.Contains("uuid", StringComparison.OrdinalIgnoreCase);

            if (!assessment.PrimaryKeyIsUuid)
                assessment.Checks.Add(new CheckResult(CheckStatus.Warn,
                    $"Primary key type '{pk.DataType}' is not CHAR/VARCHAR(36). Manual review required for UUID migration."));
        }

        // Multi-tenant candidates — caller sets IsMultiTenant after this call;
        // the warn is deferred and emitted by the command if !IsMultiTenant.
        assessment.MultiTenantCandidateColumns = columns
            .Where(c => TenantColumnPatterns.Any(p =>
                c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name)
            .ToList();

        return assessment;
    }
}
