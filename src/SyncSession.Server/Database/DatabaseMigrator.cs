using DbUp;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Reflection;

namespace SyncSession.Server.Database;

/// <summary>
/// Handles automatic database schema creation and migration on server startup.
/// Uses DbUp to track which scripts have been executed.
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>
    /// Ensures the database schema is up to date by running any pending migration scripts.
    /// </summary>
    /// <param name="connectionString">MySQL/MariaDB connection string</param>
    /// <param name="scriptsPath">Path to SQL scripts folder. Defaults to embedded scripts when null.</param>
    /// <param name="logger">Optional ILogger for structured output. Falls back to console when null.</param>
    /// <returns>True if migrations succeeded, false otherwise.</returns>
    public static bool EnsureDatabase(string connectionString, string provider = "mysql", string? scriptsPath = null, ILogger? logger = null)
    {
        var upgrader = scriptsPath == null
            ? CreateEmbeddedScriptUpgrader(connectionString, provider, logger)
            : CreateFileSystemUpgrader(connectionString, scriptsPath, logger);

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            if (logger != null)
                logger.LogError(result.Error, "SyncSystem database migration failed");
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("❌ Database migration failed:");
                Console.WriteLine(result.Error);
                Console.ResetColor();
            }
            return false;
        }

        if (result.Scripts.Any())
        {
            if (logger != null)
            {
                logger.LogInformation("SyncSystem database migration successful — {Count} script(s) applied", result.Scripts.Count());
                foreach (var script in result.Scripts)
                    logger.LogInformation("  Applied migration: {ScriptName}", script.Name);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✅ Database migration successful! Executed {result.Scripts.Count()} script(s):");
                foreach (var script in result.Scripts)
                    Console.WriteLine($"   - {script.Name}");
                Console.ResetColor();
            }
        }
        else
        {
            if (logger != null)
                logger.LogInformation("SyncSystem database migration: schema already up to date");
            else
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("ℹ️  Database is already up to date (no new scripts to run)");
                Console.ResetColor();
            }
        }

        return true;
    }

    /// <summary>
    /// Test if the database connection is valid (without running migrations).
    /// </summary>
    public static bool TestConnection(string connectionString)
    {
        try
        {
        using var connection = new MySqlConnection(connectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DbUp.Engine.UpgradeEngine CreateFileSystemUpgrader(
        string connectionString, string scriptsPath, ILogger? logger)
    {
        var fullPath = Path.GetFullPath(scriptsPath);

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Scripts directory not found: {fullPath}");

        var builder = DeployChanges.To
            .MySqlDatabase(connectionString)
            .WithScriptsFromFileSystem(fullPath, new DbUp.Engine.SqlScriptOptions
            {
                ScriptType = DbUp.Support.ScriptType.RunOnce,
                RunGroupOrder = 1
            })
            .WithTransaction();

        return (logger != null ? builder.LogTo(new MicrosoftLoggerUpgradeLog(logger)) : builder.LogToConsole())
            .Build();
    }

    private static DbUp.Engine.UpgradeEngine CreateEmbeddedScriptUpgrader(
        string connectionString, string provider, ILogger? logger)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var assemblyName = assembly.GetName().Name;

        var folderName = provider.ToLowerInvariant() switch
        {
            "mysql" or "mariadb" => "MySQL",
            "postgres"           => "PostgreSQL",
            "sqlite"             => "SQLite",
            _ => throw new InvalidOperationException($"Unsupported database provider for AutoMigrate: '{provider}'")
        };

        var scriptPrefix = $"{assemblyName}.Scripts.{folderName}.";

        var builder = provider.ToLowerInvariant() switch
        {
            "mysql" or "mariadb" => DeployChanges.To.MySqlDatabase(connectionString),
            _ => throw new InvalidOperationException($"Unsupported database provider for AutoMigrate: '{provider}'")
        };

        return (logger != null
                ? builder.WithScriptsEmbeddedInAssembly(assembly, s => s.StartsWith(scriptPrefix, StringComparison.OrdinalIgnoreCase)).WithTransaction().LogTo(new MicrosoftLoggerUpgradeLog(logger))
                : builder.WithScriptsEmbeddedInAssembly(assembly, s => s.StartsWith(scriptPrefix, StringComparison.OrdinalIgnoreCase)).WithTransaction().LogToConsole())
            .Build();
    }

    /// <summary>
    /// Bridges DbUp's <see cref="IUpgradeLog"/> to <see cref="ILogger"/>.
    /// </summary>
    private sealed class MicrosoftLoggerUpgradeLog : IUpgradeLog
    {
        private readonly ILogger _logger;
        public MicrosoftLoggerUpgradeLog(ILogger logger) => _logger = logger;

        public void WriteInformation(string format, params object[] args) =>
            _logger.LogInformation(format, args);

        public void WriteWarning(string format, params object[] args) =>
            _logger.LogWarning(format, args);

        public void WriteError(string format, params object[] args) =>
            _logger.LogError(format, args);
    }
}
