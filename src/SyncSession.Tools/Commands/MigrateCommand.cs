using System.CommandLine;
using SyncSession.Tools.Assessment;
using SyncSession.Tools.Models;

namespace SyncSession.Tools.Commands;

public static class MigrateCommand
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
            "Output directory for generated SQL scripts");
        outputDirOpt.AddAlias("-o");

        var targetOpt = new Option<string>(
            "--target", () => "both",
            "Migration target: server (MySQL ALTER), client (SQLite CREATE), or both");
        targetOpt.FromAmong("server", "client", "both");

        var cmd = new Command("migrate",
            "Generate ALTER TABLE (server/MySQL) and/or CREATE TABLE (client/SQLite) SQL scripts");
        cmd.AddOption(connectionStringOpt);
        cmd.AddOption(assemblyOpt);
        cmd.AddOption(outputDirOpt);
        cmd.AddOption(targetOpt);

        cmd.SetHandler(async (connectionString, assemblyPath, outputDir, target) =>
        {
            Console.WriteLine($"[SyncSystem] Generating migration scripts (target={target})...");

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

            var missingTables = tables.Where(t => !t.ExistsInDatabase).ToList();
            if (missingTables.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Warning: {missingTables.Count} entity table(s) not found in DB — skipped:");
                foreach (var t in missingTables) Console.WriteLine($"    - {t.TableName}");
                Console.ResetColor();
            }

            Directory.CreateDirectory(outputDir);
            var generator = new ScriptGenerator(connectionString);

            // ── Server script ─────────────────────────────────────────────
            if (target == "server" || target == "both")
            {
                var serverSql = await generator.GenerateAlterScriptAsync(tables);
                var serverPath = Path.Combine(outputDir, "03_server_alter.sql");
                await File.WriteAllTextAsync(serverPath, serverSql);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n  Server script : {serverPath}");
                Console.ResetColor();
            }

            // ── Client script ─────────────────────────────────────────────
            if (target == "client" || target == "both")
            {
                var clientSql = await generator.GenerateSqliteCreateScriptAsync(tables);
                var clientPath = Path.Combine(outputDir, "04_client_sqlite.sql");
                await File.WriteAllTextAsync(clientPath, clientSql);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Client script : {clientPath}");
                Console.ResetColor();
            }

            Console.WriteLine("\nDone. Review scripts before executing against your databases.");

        }, connectionStringOpt, assemblyOpt, outputDirOpt, targetOpt);

        return cmd;
    }
}
