using System.CommandLine;
using MySqlConnector;
using SyncSession.Tools.Assessment;
using SyncSession.Tools.Models;

namespace SyncSession.Tools.Commands;

public static class ValidateCommand
{
    public static Command Build()
    {
        var connectionStringOpt = new Option<string>(
            "--connection-string", "MySQL/MariaDB connection string to validate") { IsRequired = true };
        connectionStringOpt.AddAlias("-c");

        var assemblyOpt = new Option<string>(
            "--assembly", "Path to the .dll containing ISyncEntity classes") { IsRequired = true };
        assemblyOpt.AddAlias("-a");

        var outputDirOpt = new Option<string>(
            "--output-dir", () => Directory.GetCurrentDirectory(),
            "Output directory for the validation report");
        outputDirOpt.AddAlias("-o");

        var cmd = new Command("validate",
            "Validate a migrated database is fully SyncSession-ready");
        cmd.AddOption(connectionStringOpt);
        cmd.AddOption(assemblyOpt);
        cmd.AddOption(outputDirOpt);

        cmd.SetHandler(async (connectionString, assemblyPath, outputDir) =>
        {
            Console.WriteLine("[SyncSession] Validating migrated database...");

            // ── Assembly scan ─────────────────────────────────────────────
            List<(string ClassName, string? TableName, bool IsMultiTenant)> entities;
            try
            {
                entities = new AssemblyScanner(assemblyPath).ScanEntities();
                Console.WriteLine($"  {entities.Count} ISyncEntity type(s) found.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Assembly scan failed: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            // ── Per-table validation ──────────────────────────────────────
            var inspector = new SchemaInspector(connectionString);
            List<string> dbTables;
            try { dbTables = await inspector.GetAllTableNamesAsync(); }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"DB connection failed: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            var result = new AssessmentResult
            {
                ConnectionString = connectionString,
                AssemblyPath = assemblyPath,
                Mode = "validate"
            };

            var assessedTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (className, tableName, isMultiTenant) in entities)
            {
                if (tableName == null) { result.UnmatchedEntityClasses.Add(className); continue; }

                if (!dbTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
                {
                    result.Tables.Add(new TableAssessment
                    {
                        TableName = tableName,
                        EntityClassName = className,
                        ExistsInDatabase = false,
                        Checks = [new CheckResult(CheckStatus.Fail, $"Table '{tableName}' not found")]
                    });
                    continue;
                }

                var assessment = await inspector.AssessTableAsync(tableName);
                assessment.EntityClassName = className;
                assessment.IsMultiTenant = isMultiTenant;

                // Emit multi-tenant warn only if entity doesn't already implement IMultiTenantSyncEntity
                if (assessment.MultiTenantCandidateColumns.Count > 0 && !isMultiTenant)
                    assessment.Checks.Add(new CheckResult(CheckStatus.Warn,
                        $"Possible multi-tenant columns detected: {string.Join(", ", assessment.MultiTenantCandidateColumns)}. " +
                        "Consider implementing IMultiTenantSyncEntity."));

                // Extra: check SyncSystem indexes exist
                var indexCheck = await CheckSyncIndexesAsync(connectionString, tableName, isMultiTenant);
                assessment.Checks.AddRange(indexCheck);

                // Extra: check no NULL in ModifiedByUserId
                var nullCheck = await CheckNoNullsAsync(connectionString, tableName, "ModifiedByUserId");
                assessment.Checks.Add(nullCheck);

                result.Tables.Add(assessment);
                assessedTableNames.Add(tableName);
            }

            result.UnmatchedDbTables = dbTables
                .Where(t => !assessedTableNames.Contains(t) && !SyncSessionTables.IsInfrastructure(t))
                .ToList();

            // ── Report ────────────────────────────────────────────────────
            Directory.CreateDirectory(outputDir);
            var reportPath = Path.Combine(outputDir, "05_validation_report.txt");
            AssessmentReporter.WriteReport(result, reportPath);

            var color = result.OverallStatus switch
            {
                CheckStatus.Pass => ConsoleColor.Green,
                CheckStatus.Warn => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            };
            Console.ForegroundColor = color;
            Console.WriteLine($"\nResult: {result.OverallStatus.ToString().ToUpperInvariant()} " +
                              $"({result.PassCount} pass, {result.WarnCount} warn, {result.FailCount} fail)");
            Console.ResetColor();
            Console.WriteLine($"Report : {reportPath}");

            Environment.Exit(result.OverallStatus == CheckStatus.Fail ? 2
                           : result.OverallStatus == CheckStatus.Warn ? 1 : 0);

        }, connectionStringOpt, assemblyOpt, outputDirOpt);

        return cmd;
    }

    /// <summary>
    /// Reports whether the table carries an index the pull path can use.
    /// </summary>
    /// <remarks>
    /// Decided by ordered columns, never by index name: an operator who created the right index under
    /// their own name has nothing missing, and a name this repo has never generated is not evidence of
    /// anything. The previous version looked for <c>IX_&lt;T&gt;_SyncSessionId</c> and
    /// <c>IX_&lt;T&gt;_ModifiedByUserId</c> — no script, generator or sample here has ever created
    /// either, so it warned falsely on every table since it was written.
    /// <para>
    /// The required set mirrors <c>MySqlServerDatabase.RequiredSyncIndexColumns</c>: <c>SyncSessionId</c>,
    /// plus <c>TenantId</c> for a multi-tenant entity. The rule is stated in both places on purpose —
    /// extracting it into a shared component was considered and rejected as the heavier option — so a
    /// change to one belongs in the other on the same day.
    /// </para>
    /// </remarks>
    private static async Task<List<CheckResult>> CheckSyncIndexesAsync(
        string connectionString, string tableName, bool isMultiTenant)
    {
        var required = isMultiTenant
            ? new[] { "SyncSessionId", "TenantId" }
            : new[] { "SyncSessionId" };

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT INDEX_NAME, COLUMN_NAME
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table
            ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        cmd.Parameters.AddWithValue("@db", conn.Database);
        cmd.Parameters.AddWithValue("@table", tableName);

        var byIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var name = r.GetString(0);
                if (!byIndex.TryGetValue(name, out var columns))
                {
                    columns = new List<string>();
                    byIndex[name] = columns;
                }
                columns.Add(r.GetString(1));
            }
        }

        var satisfying = byIndex.FirstOrDefault(ix =>
            ix.Value.Count >= required.Length &&
            ix.Value.Take(required.Length).SequenceEqual(required, StringComparer.OrdinalIgnoreCase));

        var columnList = string.Join(", ", required);

        return
        [
            satisfying.Key is not null
                ? new CheckResult(CheckStatus.Pass, $"Pull index present: '{satisfying.Key}' ({columnList})")
                : new CheckResult(CheckStatus.Warn,
                    $"No index leads with ({columnList}) — every pull scans this table. " +
                    "SyncSession creates it shortly after the app starts, unless ManageIndexes is disabled.")
        ];
    }

    private static async Task<CheckResult> CheckNoNullsAsync(
        string connectionString, string tableName, string column)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM `{tableName}` WHERE `{column}` IS NULL";

        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        return count == 0
            ? new CheckResult(CheckStatus.Pass, $"No NULL values in {column}")
            : new CheckResult(CheckStatus.Fail, $"{count} row(s) with NULL {column} — backfill required");
    }
}
