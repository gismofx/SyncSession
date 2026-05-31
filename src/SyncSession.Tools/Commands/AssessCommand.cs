using System.CommandLine;
using SyncSession.Tools.Assessment;
using SyncSession.Tools.Models;

namespace SyncSession.Tools.Commands;

public static class AssessCommand
{
    public static Command Build()
    {
        var connectionStringOpt = new Option<string>(
            "--connection-string", "MySQL/MariaDB connection string") { IsRequired = true };
        connectionStringOpt.AddAlias("-c");

        var assemblyOpt = new Option<string>(
            "--assembly", "Path to the .dll containing ISyncEntity classes") { IsRequired = true };
        assemblyOpt.AddAlias("-a");

        var outputDirOpt = new Option<string>(
            "--output-dir", () => Directory.GetCurrentDirectory(),
            "Output directory for the report file");
        outputDirOpt.AddAlias("-o");

        var modeOpt = new Option<string>(
            "--mode", () => "clone",
            "Migration mode: clone (recommended) or inplace");
        modeOpt.FromAmong("clone", "inplace");

        var verboseOpt = new Option<bool>(
            "--verbose", () => false,
            "Print raw entity scan results before DB cross-reference");
        verboseOpt.AddAlias("-v");

        var cmd = new Command("assess", "Assess an existing database and entity assembly for SyncSystem migration readiness");
        cmd.AddOption(connectionStringOpt);
        cmd.AddOption(assemblyOpt);
        cmd.AddOption(outputDirOpt);
        cmd.AddOption(modeOpt);
        cmd.AddOption(verboseOpt);

        cmd.SetHandler(async (connectionString, assemblyPath, outputDir, mode, verbose) =>
        {
            Console.WriteLine($"[SyncSystem] Assessing: {connectionString}");
            Console.WriteLine($"[SyncSystem] Assembly : {assemblyPath}");
            Console.WriteLine();

            var result = new AssessmentResult
            {
                ConnectionString = connectionString,
                AssemblyPath = assemblyPath,
                Mode = mode
            };

            // ── Assembly scan ─────────────────────────────────────────────────
            Console.Write("[SyncSystem] Scanning assembly... ");
            List<(string ClassName, string? TableName, bool IsMultiTenant)> entities;
            try
            {
                var scanner = new AssemblyScanner(assemblyPath);
                entities = scanner.ScanEntities();
                Console.WriteLine($"{entities.Count} ISyncEntity type(s) found.");

                if (verbose)
                {
                    Console.WriteLine();
                    foreach (var (className, tableName, isMultiTenant) in entities)
                        Console.WriteLine($"    {className,-30} → table: {tableName ?? "(none)"}{(isMultiTenant ? "  [multi-tenant]" : "")}");
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILED: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            // ── Schema inspection ─────────────────────────────────────────────
            Console.Write("[SyncSystem] Inspecting database schema... ");
            List<string> dbTables;
            try
            {
                var inspector = new SchemaInspector(connectionString);
                dbTables = await inspector.GetAllTableNamesAsync();
                Console.WriteLine($"{dbTables.Count} table(s) found.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAILED: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            // ── Cross-reference + assess each table ───────────────────────────
            var inspector2 = new SchemaInspector(connectionString);
            var entityByTable = entities
                .Where(e => e.TableName != null)
                .ToDictionary(e => e.TableName!, e => e, StringComparer.OrdinalIgnoreCase);

            var assessedTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (className, tableName, isMultiTenant) in entities)
            {
                if (tableName == null)
                {
                    result.UnmatchedEntityClasses.Add(className);
                    continue;
                }

                var assessment = dbTables.Contains(tableName, StringComparer.OrdinalIgnoreCase)
                    ? await inspector2.AssessTableAsync(tableName)
                    : new TableAssessment
                    {
                        TableName = tableName,
                        ExistsInDatabase = false,
                        Checks = [new CheckResult(CheckStatus.Fail, $"Table '{tableName}' not found in database")]
                    };

                assessment.EntityClassName = className;
                assessment.HasMatchingEntity = true;
                assessment.IsMultiTenant = isMultiTenant;

                // Emit multi-tenant warn only if entity doesn't already implement IMultiTenantSyncEntity
                if (assessment.MultiTenantCandidateColumns.Count > 0 && !isMultiTenant)
                    assessment.Checks.Add(new CheckResult(CheckStatus.Warn,
                        $"Possible multi-tenant columns detected: {string.Join(", ", assessment.MultiTenantCandidateColumns)}. " +
                        "Consider implementing IMultiTenantSyncEntity."));
                assessedTableNames.Add(tableName);
                result.Tables.Add(assessment);
            }

            result.UnmatchedDbTables = dbTables
                .Where(t => !assessedTableNames.Contains(t) && !SyncSessionTables.IsInfrastructure(t))
                .ToList();

            // ── Write report ──────────────────────────────────────────────────
            Directory.CreateDirectory(outputDir);
            var reportPath = Path.Combine(outputDir, "01_assessment_report.txt");
            AssessmentReporter.WriteReport(result, reportPath);

            // ── Console summary ───────────────────────────────────────────────
            Console.WriteLine();
            var color = result.OverallStatus switch
            {
                CheckStatus.Pass => ConsoleColor.Green,
                CheckStatus.Warn => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            };
            Console.ForegroundColor = color;
            Console.WriteLine($"Result: {result.OverallStatus.ToString().ToUpperInvariant()} " +
                              $"({result.PassCount} pass, {result.WarnCount} warn, {result.FailCount} fail)");
            Console.ResetColor();
            Console.WriteLine($"Report : {reportPath}");

            Environment.Exit(result.OverallStatus == CheckStatus.Fail ? 2
                           : result.OverallStatus == CheckStatus.Warn ? 1 : 0);

        }, connectionStringOpt, assemblyOpt, outputDirOpt, modeOpt, verboseOpt);

        return cmd;
    }
}
