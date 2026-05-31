using System.CommandLine;
using SyncSession.Tools.Assessment;
using SyncSession.Tools.Models;

namespace SyncSession.Tools.Commands;

public static class CloneCommand
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
            "Output directory for generated SQL script");
        outputDirOpt.AddAlias("-o");

        var targetDbOpt = new Option<string?>(
            "--target-db", () => null,
            "Target database name (default: <sourcedb>_syncsystem)");

        var cmd = new Command("clone",
            "Generate a CREATE DATABASE + CREATE TABLE SQL script (clone mode migration)");
        cmd.AddOption(connectionStringOpt);
        cmd.AddOption(assemblyOpt);
        cmd.AddOption(outputDirOpt);
        cmd.AddOption(targetDbOpt);

        cmd.SetHandler(async (connectionString, assemblyPath, outputDir, targetDb) =>
        {
            Console.WriteLine("[SyncSystem] Generating clone script...");

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

            // ── Schema inspection ─────────────────────────────────────────
            var inspector = new SchemaInspector(connectionString);
            List<string> dbTables;
            try
            {
                dbTables = await inspector.GetAllTableNamesAsync();
                Console.WriteLine($"  {dbTables.Count} DB table(s) found.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"DB connection failed: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            // ── Assess matched tables ─────────────────────────────────────
            var tables = new List<TableAssessment>();
            foreach (var (_, tableName, isMultiTenant) in entities)
            {
                if (tableName == null) continue;
                var assessment = dbTables.Contains(tableName, StringComparer.OrdinalIgnoreCase)
                    ? await inspector.AssessTableAsync(tableName)
                    : new TableAssessment { TableName = tableName, ExistsInDatabase = false };
                assessment.IsMultiTenant = isMultiTenant;
                tables.Add(assessment);
            }

            var missing = tables.Where(t => !t.ExistsInDatabase).ToList();
            if (missing.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Warning: {missing.Count} entity table(s) not found in DB — skipped:");
                foreach (var t in missing) Console.WriteLine($"    - {t.TableName}");
                Console.ResetColor();
            }

            // ── Resolve target DB name ────────────────────────────────────
            var builder = new MySqlConnector.MySqlConnectionStringBuilder(connectionString);
            var sourceDb = builder.Database;
            var resolvedTargetDb = targetDb ?? $"{sourceDb}_syncsystem";

            // ── Generate script ───────────────────────────────────────────
            var generator = new ScriptGenerator(connectionString);
            string sql;
            try
            {
                sql = await generator.GenerateCloneScriptAsync(tables, resolvedTargetDb);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Script generation failed: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            // ── Write output ──────────────────────────────────────────────
            Directory.CreateDirectory(outputDir);
            var scriptPath = Path.Combine(outputDir, "02_clone_script.sql");
            await File.WriteAllTextAsync(scriptPath, sql);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nScript : {scriptPath}");
            Console.ResetColor();
            Console.WriteLine($"Target DB: {resolvedTargetDb}");
            Console.WriteLine("Run this script against your MySQL server to create the SyncSystem-ready clone.");

        }, connectionStringOpt, assemblyOpt, outputDirOpt, targetDbOpt);

        return cmd;
    }
}
