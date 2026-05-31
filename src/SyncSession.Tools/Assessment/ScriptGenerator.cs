using MySqlConnector;
using SyncSession.Tools.Models;

namespace SyncSession.Tools.Assessment;

/// <summary>
/// Reads column/index metadata from INFORMATION_SCHEMA and generates
/// migration SQL (clone or in-place ALTER) for SyncSystem readiness.
/// </summary>
public class ScriptGenerator
{
    private static readonly (string Column, string Definition)[] SyncColumns =
    [
        ("IsDeleted",        "TINYINT(1) NOT NULL DEFAULT 0"),
        ("ModifiedByUserId", "VARCHAR(100) NOT NULL DEFAULT 'System'"),
        ("ModifiedAtUtc",    "DATETIME(6) NOT NULL DEFAULT UTC_TIMESTAMP(6)"),
        ("SyncSessionId",    "VARCHAR(36) NULL"),
    ];

    // Column names (case-insensitive) treated as soft-delete equivalents of IsDeleted.
    // Matched column value is copied into IsDeleted during backfill.
    // IsActive is inverted: IsDeleted = NOT IsActive.
    private static readonly string[] SoftDeleteCandidates =
        ["FlagDelete", "Deleted", "IsRemoved", "Removed"];

    // IsActive columns are inverted (IsDeleted = 1 - IsActive).
    private static readonly string[] InvertedSoftDeleteCandidates =
        ["IsActive", "Active"];

    private readonly string _connectionString;

    public ScriptGenerator(string connectionString) =>
        _connectionString = connectionString;

    // ── Public entry points ───────────────────────────────────────────────

    /// <summary>
    /// Generates a complete CREATE DATABASE + CREATE TABLE script for clone mode.
    /// The new database name is appended with _syncsystem.
    /// </summary>
    public async Task<string> GenerateCloneScriptAsync(
        IEnumerable<TableAssessment> tables,
        string newDatabaseName)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        var sourceDb = conn.Database;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- SyncSystem Clone Script");
        sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"-- Source DB : {sourceDb}");
        sb.AppendLine($"-- Target DB : {newDatabaseName}");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();
        sb.AppendLine($"CREATE DATABASE IF NOT EXISTS `{newDatabaseName}`");
        sb.AppendLine("  CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
        sb.AppendLine($"USE `{newDatabaseName}`;");
        sb.AppendLine();

        foreach (var table in tables.Where(t => t.ExistsInDatabase))
        {
            var ddl = await BuildCreateTableAsync(conn, sourceDb, table.TableName);
            sb.AppendLine(ddl);
            sb.AppendLine();
        }

        sb.AppendLine("-- Add indexes for SyncSystem columns");
        foreach (var table in tables.Where(t => t.ExistsInDatabase))
        {
            sb.AppendLine($"ALTER TABLE `{table.TableName}`");
            sb.AppendLine($"  ADD INDEX `IX_{table.TableName}_SyncSessionId` (`SyncSessionId`),");
            sb.AppendLine($"  ADD INDEX `IX_{table.TableName}_ModifiedByUserId` (`ModifiedByUserId`);");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates ALTER TABLE + backfill statements for in-place server migration.
    /// </summary>
    public async Task<string> GenerateAlterScriptAsync(
        IEnumerable<TableAssessment> tables)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- SyncSystem In-Place ALTER Script (Server / MySQL)");
        sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();

        foreach (var table in tables.Where(t => t.ExistsInDatabase))
        {
            var missing = table.MissingSyncColumns;
            if (missing.Count == 0) continue;

            sb.AppendLine($"-- Table: {table.TableName}");
            sb.AppendLine($"ALTER TABLE `{table.TableName}`");

            var clauses = new List<string>();
            foreach (var (col, def) in SyncColumns.Where(c => missing.Contains(c.Column)))
                clauses.Add($"  ADD COLUMN `{col}` {def}");

            clauses.Add($"  ADD INDEX IF NOT EXISTS `IX_{table.TableName}_SyncSessionId` (`SyncSessionId`)");
            clauses.Add($"  ADD INDEX IF NOT EXISTS `IX_{table.TableName}_ModifiedByUserId` (`ModifiedByUserId`)");

            sb.AppendLine(string.Join(",\n", clauses) + ";");
            sb.AppendLine();

            // ── Backfill UPDATEs ──────────────────────────────────────────
            sb.AppendLine($"-- Backfill: {table.TableName}");

            if (missing.Contains("IsDeleted"))
            {
                var directMatch = table.ExistingColumns
                    .FirstOrDefault(c => SoftDeleteCandidates
                        .Any(s => s.Equals(c, StringComparison.OrdinalIgnoreCase)));

                var invertedMatch = table.ExistingColumns
                    .FirstOrDefault(c => InvertedSoftDeleteCandidates
                        .Any(s => s.Equals(c, StringComparison.OrdinalIgnoreCase)));

                if (directMatch != null)
                    sb.AppendLine($"UPDATE `{table.TableName}` SET `IsDeleted` = `{directMatch}`;  -- copied from {directMatch}");
                else if (invertedMatch != null)
                    sb.AppendLine($"UPDATE `{table.TableName}` SET `IsDeleted` = CASE WHEN `{invertedMatch}` = 1 THEN 0 ELSE 1 END;  -- inverted from {invertedMatch}");
                else
                    sb.AppendLine($"UPDATE `{table.TableName}` SET `IsDeleted` = 0;  -- TODO: verify no pre-existing soft-deleted rows");
            }

            if (missing.Contains("ModifiedByUserId"))
                sb.AppendLine($"UPDATE `{table.TableName}` SET `ModifiedByUserId` = 'Migration';");

            if (missing.Contains("ModifiedAtUtc"))
                sb.AppendLine($"UPDATE `{table.TableName}` SET `ModifiedAtUtc` = UTC_TIMESTAMP(6);");

            if (missing.Contains("SyncSessionId"))
                sb.AppendLine($"UPDATE `{table.TableName}` SET `SyncSessionId` = NULL;");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates SQLite CREATE TABLE scripts for the client-side database.
    /// Column types are derived from MySQL column types via a simple mapping.
    /// </summary>
    public async Task<string> GenerateSqliteCreateScriptAsync(
        IEnumerable<TableAssessment> tables)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        var sourceDb = conn.Database;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- SyncSystem SQLite Client Schema Script");
        sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"-- Source DB : {sourceDb}");
        sb.AppendLine("-- Apply this to each client's SQLite database.");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();

        foreach (var table in tables.Where(t => t.ExistsInDatabase))
        {
            var columns = await GetColumnsAsync(conn, sourceDb, table.TableName);
            var pkCols  = await GetPrimaryKeyColumnsAsync(conn, sourceDb, table.TableName);
            var existingNames = columns.Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            sb.AppendLine($"CREATE TABLE IF NOT EXISTS `{table.TableName}` (");
            var defs = new List<string>();

            foreach (var col in columns)
            {
                var sqliteType = ToSqliteType(col.ColumnType);
                var notNull = !col.IsNullable ? " NOT NULL" : "";
                var pk = pkCols.Count == 1 && pkCols[0].Equals(col.Name, StringComparison.OrdinalIgnoreCase)
                    ? " PRIMARY KEY" : "";
                var def = $"  `{col.Name}` {sqliteType}{pk}{notNull}";
                if (col.Default != null && !col.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase))
                    def += $" DEFAULT {MapSqliteDefault(col.Default, col.ColumnType)}";
                defs.Add(def);
            }

            // Add client-side sync columns not already present
            if (!existingNames.Contains("IsDirty"))
                defs.Add("  `IsDirty` INTEGER NOT NULL DEFAULT 0");
            if (!existingNames.Contains("ModifiedByUserId"))
                defs.Add("  `ModifiedByUserId` TEXT NOT NULL DEFAULT 'Local'");
            if (!existingNames.Contains("ModifiedAtUtc"))
                defs.Add("  `ModifiedAtUtc` TEXT NOT NULL DEFAULT (datetime('now'))");
            if (!existingNames.Contains("IsDeleted"))
                defs.Add("  `IsDeleted` INTEGER NOT NULL DEFAULT 0");

            if (pkCols.Count > 1)
                defs.Add($"  PRIMARY KEY ({string.Join(", ", pkCols.Select(c => $"`{c}`"))})");

            sb.Append(string.Join(",\n", defs));
            sb.AppendLine();
            sb.AppendLine(");");
            sb.AppendLine($"CREATE INDEX IF NOT EXISTS `IX_{table.TableName}_IsDirty` ON `{table.TableName}` (`IsDirty`);");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ToSqliteType(string mysqlType)
    {
        var lower = mysqlType.ToLowerInvariant();
        if (lower.Contains("int")) return "INTEGER";
        if (lower.Contains("char") || lower.Contains("text") || lower.Contains("uuid")
            || lower.Contains("enum") || lower.Contains("set")) return "TEXT";
        if (lower.Contains("float") || lower.Contains("double") || lower.Contains("decimal")
            || lower.Contains("numeric")) return "REAL";
        if (lower.Contains("date") || lower.Contains("time")) return "TEXT";
        if (lower.Contains("blob") || lower.Contains("binary")) return "BLOB";
        return "TEXT";
    }

    private static string MapSqliteDefault(string mysqlDefault, string columnType)
    {
        // UTC_TIMESTAMP / NOW() → SQLite equivalent
        if (mysqlDefault.Contains("UTC_TIMESTAMP", StringComparison.OrdinalIgnoreCase)
            || mysqlDefault.Contains("NOW()", StringComparison.OrdinalIgnoreCase)
            || mysqlDefault.Contains("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
            return "(datetime('now'))";
        return mysqlDefault;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<string> BuildCreateTableAsync(
        MySqlConnection conn, string sourceDb, string tableName)
    {
        var columns = await GetColumnsAsync(conn, sourceDb, tableName);
        var pkCols  = await GetPrimaryKeyColumnsAsync(conn, sourceDb, tableName);

        var existingNames = columns.Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CREATE TABLE `{tableName}` (");

        var defs = new List<string>();
        foreach (var col in columns)
            defs.Add($"  {BuildColumnDef(col)}");

        // Append missing SyncSystem columns
        foreach (var (colName, colDef) in SyncColumns)
        {
            if (!existingNames.Contains(colName))
                defs.Add($"  `{colName}` {colDef}");
        }

        if (pkCols.Count > 0)
            defs.Add($"  PRIMARY KEY ({string.Join(", ", pkCols.Select(c => $"`{c}`"))})");

        sb.Append(string.Join(",\n", defs));
        sb.AppendLine();
        sb.Append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

        return sb.ToString();
    }

    private static string BuildColumnDef(ColumnInfo col)
    {
        var sb = new System.Text.StringBuilder($"  `{col.Name}` {col.ColumnType}");
        if (!col.IsNullable) sb.Append(" NOT NULL");
        else sb.Append(" NULL");
        if (col.Default != null)
            sb.Append($" DEFAULT {col.Default}");
        if (col.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase))
            sb.Append(" AUTO_INCREMENT");
        return sb.ToString().TrimStart();
    }

    private async Task<List<ColumnInfo>> GetColumnsAsync(
        MySqlConnection conn, string db, string table)
    {
        var cols = new List<ColumnInfo>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("@db", db);
        cmd.Parameters.AddWithValue("@table", table);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            cols.Add(new ColumnInfo(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2) == "YES",
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4)));
        return cols;
    }

    private async Task<List<string>> GetPrimaryKeyColumnsAsync(
        MySqlConnection conn, string db, string table)
    {
        var pks = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
              AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("@db", db);
        cmd.Parameters.AddWithValue("@table", table);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) pks.Add(r.GetString(0));
        return pks;
    }

    private record ColumnInfo(
        string Name, string ColumnType, bool IsNullable,
        string? Default, string Extra);
}
