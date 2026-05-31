using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SyncSession.Samples.Console.Infrastructure;
using SyncSession.Samples.Console.Scenarios;

namespace SyncSession.Samples.Console;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            // Parse arguments
            var options = ParseArguments(args);

            if (options.ShowHelp)
            {
                DisplayHelp();
                return 0;
            }

            // Display header
            OutputHelper.WriteHeader("SyncSystem Console Demo");

            // Check server health first
            var healthCheck = new ServerHealthCheck(options.ServerUrl);
            if (!await healthCheck.CheckAsync())
            {
                healthCheck.DisplayServerNotRunningMessage();
                return 1;
            }

            OutputHelper.WriteSuccess("Server is running!");
            
            // Show verbose mode status
            if (options.Verbose)
            {
                var verboseReason = System.Diagnostics.Debugger.IsAttached 
                    ? "Verbose mode enabled (debugger detected)"
                    : "Verbose mode enabled (--verbose flag)";
                OutputHelper.WriteInfo(verboseReason);
            }
            
            OutputHelper.WriteBlankLine();

            // If command-line scenario specified, run it and exit
            if (!string.IsNullOrEmpty(options.Scenario) && args.Length > 0)
            {
                OutputHelper.WriteInfo($"Running scenario: {options.Scenario}");
                OutputHelper.WriteInfo($"Server: {options.ServerUrl}");
                OutputHelper.WriteInfo($"Records: {options.RecordCount}");
                if (options.PersistDatabases)
                {
                    OutputHelper.WriteWarning("Databases will be preserved after exit (--persist)");
                }
                OutputHelper.WriteBlankLine();

                var exitCode = options.RunAllScenarios 
                    ? await RunAllScenariosAsync(options) 
                    : await RunScenarioAsync(options.Scenario, options);

                if (!options.PersistDatabases && exitCode == 0)
                {
                    CleanupDatabases();
                }

                OutputHelper.WriteBlankLine();
                OutputHelper.WriteSuccess("Demo complete!");
                return exitCode;
            }

            // Otherwise, run interactive menu mode
            return await RunInteractiveMenuAsync(options);
        }
        catch (Exception ex)
        {
            OutputHelper.WriteBlankLine();
            OutputHelper.WriteError($"Fatal error: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    static async Task<int> RunInteractiveMenuAsync(ProgramOptions options)
    {
        while (true)
        {
            OutputHelper.WriteBlankLine();
            OutputHelper.WriteDivider('=', 70);
            OutputHelper.WriteHeader("Select a Scenario");
            System.Console.WriteLine();
            System.Console.WriteLine("  1. Simple Scenario       - Basic push/pull sync");
            System.Console.WriteLine("  2. Multi-Client          - Concurrent sync (proves session-based tracking)");
            System.Console.WriteLine("  3. Failure Recovery      - Network failure resilience");
            System.Console.WriteLine("  4. Run All Scenarios     - Execute all 3 in sequence");
            System.Console.WriteLine();
            System.Console.WriteLine("  0. Exit");
            System.Console.WriteLine();
            OutputHelper.WriteDivider('-', 70);
            System.Console.Write("Enter your choice (0-4): ");

            var input = System.Console.ReadLine()?.Trim();

            if (input == "0")
            {
                OutputHelper.WriteBlankLine();
                OutputHelper.WriteInfo("Cleaning up...");
                if (!options.PersistDatabases)
                {
                    CleanupDatabases();
                }
                OutputHelper.WriteSuccess("Goodbye!");
                return 0;
            }

            string? scenario = input switch
            {
                "1" => "simple",
                "2" => "multi",
                "3" => "failure",
                "4" => "all",
                _ => null
            };

            if (scenario == null)
            {
                OutputHelper.WriteWarning("Invalid choice. Please enter 0-4.");
                continue;
            }

            OutputHelper.WriteBlankLine();
            OutputHelper.WriteDivider('=', 70);

            try
            {
                if (scenario == "all")
                {
                    await RunAllScenariosAsync(options);
                }
                else
                {
                    await RunScenarioAsync(scenario, options);
                }

                OutputHelper.WriteBlankLine();
                OutputHelper.WriteSuccess("Scenario complete!");
            }
            catch (Exception ex)
            {
                OutputHelper.WriteBlankLine();
                OutputHelper.WriteError($"Scenario failed: {ex.Message}");
            }

            OutputHelper.WriteBlankLine();
            OutputHelper.WriteInfo("Press any key to return to menu...");
            System.Console.ReadKey(true);
        }
    }

    static ProgramOptions ParseArguments(string[] args)
    {
        var options = new ProgramOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;

                case "--scenario":
                case "-s":
                    if (i + 1 < args.Length)
                        options.Scenario = args[++i];
                    break;

                case "--all":
                case "-a":
                    options.RunAllScenarios = true;
                    break;

                case "--persist":
                case "-p":
                    options.PersistDatabases = true;
                    break;

                case "--verbose":
                case "-v":
                    options.Verbose = true;
                    break;

                case "--records":
                case "-r":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var count))
                        options.RecordCount = count;
                    break;

                case "--batch-size":
                case "-b":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var size))
                        options.BatchSize = size;
                    break;

                case "--server":
                    if (i + 1 < args.Length)
                        options.ServerUrl = args[++i];
                    break;
            }
        }

        // Load from appsettings.json if server URL not specified
        if (string.IsNullOrEmpty(options.ServerUrl))
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .Build();

            options.ServerUrl = config["SyncServer:BaseUrl"] ?? "https://localhost:5001";
        }

        // Auto-enable verbose mode when running under debugger
        if (System.Diagnostics.Debugger.IsAttached && !options.Verbose)
        {
            options.Verbose = true;
        }

        return options;
    }

    static void DisplayHelp()
    {
        OutputHelper.WriteHeader("SyncSystem Console Demo - Help");

        System.Console.WriteLine("Usage: dotnet run [options]");
        System.Console.WriteLine();
        System.Console.WriteLine("Options:");
        System.Console.WriteLine("  --scenario, -s <name>    Scenario to run: simple, multi, failure (default: simple)");
        System.Console.WriteLine("  --all, -a                Run all scenarios");
        System.Console.WriteLine("  --persist, -p            Keep database files after exit");
        System.Console.WriteLine("  --verbose, -v            Show detailed error information and stack traces");
        System.Console.WriteLine("  --records, -r <count>    Number of records to generate (default: 10)");
        System.Console.WriteLine("  --batch-size, -b <size>  Sync batch size (default: 1000)");
        System.Console.WriteLine("  --server <url>           Server URL (default: https://localhost:5001)");
        System.Console.WriteLine("  --help, -h               Show this help message");
        System.Console.WriteLine();
        System.Console.WriteLine("Interactive Mode (default):");
        System.Console.WriteLine("  dotnet run                  # Shows menu to select scenarios");
        System.Console.WriteLine();
        System.Console.WriteLine("Command-Line Mode:");
        System.Console.WriteLine("  dotnet run --scenario <n>   # Runs specific scenario and exits");
        System.Console.WriteLine();
        System.Console.WriteLine("Examples:");
        System.Console.WriteLine("  dotnet run                              # Run simple scenario");
        System.Console.WriteLine("  dotnet run --scenario multi             # Run multi-client scenario");
        System.Console.WriteLine("  dotnet run --all                        # Run all scenarios");
        System.Console.WriteLine("  dotnet run --records 50 --persist       # 50 records, keep databases");
        System.Console.WriteLine("  dotnet run --server https://other.com   # Use different server");
        System.Console.WriteLine();
    }

    static async Task<int> RunAllScenariosAsync(ProgramOptions options)
    {
        var scenarios = new[] { "simple", "multi", "failure" };
        var results = new Dictionary<string, bool>();

        for (int i = 0; i < scenarios.Length; i++)
        {
            var scenario = scenarios[i];

            OutputHelper.WriteBlankLine();
            OutputHelper.WriteDivider('=', 70);
            OutputHelper.WriteHeader($"Scenario {i + 1}/{scenarios.Length}: {scenario.ToUpperInvariant()}");

            var exitCode = await RunScenarioAsync(scenario, options);
            results[scenario] = exitCode == 0;

            if (i < scenarios.Length - 1)
            {
                OutputHelper.WriteBlankLine();
                OutputHelper.WriteInfo("Press any key to continue to next scenario...");
                System.Console.ReadKey(true);
            }
        }

        // Summary
        OutputHelper.WriteBlankLine();
        OutputHelper.WriteDivider('=', 70);
        OutputHelper.WriteHeader("Summary");

        foreach (var (scenario, success) in results)
        {
            if (success)
                OutputHelper.WriteSuccess($"{scenario.ToUpperInvariant()}: PASSED");
            else
                OutputHelper.WriteError($"{scenario.ToUpperInvariant()}: FAILED");
        }

        return results.Values.All(x => x) ? 0 : 1;
    }

    static async Task<int> RunScenarioAsync(string scenarioName, ProgramOptions options)
    {
        try
        {
            switch (scenarioName.ToLowerInvariant())
            {
                case "simple":
                    await RunSimpleScenarioAsync(options);
                    break;

                case "multi":
                    await RunMultiClientScenarioAsync(options);
                    break;

                case "failure":
                    await RunFailureRecoveryScenarioAsync(options);
                    break;

                default:
                    OutputHelper.WriteError($"Unknown scenario: {scenarioName}");
                    OutputHelper.WriteInfo("Valid scenarios: simple, multi, failure");
                    return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            OutputHelper.WriteBlankLine();
            OutputHelper.WriteError($"Scenario failed: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // Placeholder scenario methods (will be implemented in Session 17c)
    static async Task RunSimpleScenarioAsync(ProgramOptions options)
    {
        await SimpleScenario.RunAsync(options);
    }

    static async Task RunMultiClientScenarioAsync(ProgramOptions options)
    {
        await MultiClientScenario.RunAsync(options);
    }

    static async Task RunFailureRecoveryScenarioAsync(ProgramOptions options)
    {
        await FailureRecoveryScenario.RunAsync(options);
    }

    static void CleanupDatabases()
    {
        OutputHelper.WriteBlankLine();
        OutputHelper.WriteInfo("Cleaning up database files...");

        // Clear SQLite connection pools to release file locks
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        
        // Small delay to ensure OS releases file handles
        System.Threading.Thread.Sleep(100);

        var patterns = new[] { "client*.db", "client*.db-wal", "client*.db-shm" };
        var currentDir = Directory.GetCurrentDirectory();

        foreach (var pattern in patterns)
        {
            var files = Directory.GetFiles(currentDir, pattern);
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    OutputHelper.WriteSuccess($"Deleted: {Path.GetFileName(file)}");
                }
                catch
                {
                    OutputHelper.WriteWarning($"Could not delete: {Path.GetFileName(file)}");
                }
            }
        }
    }
}

/// <summary>
/// Command-line options for the demo application
/// </summary>
public class ProgramOptions
{
    public bool ShowHelp { get; set; }
    public string Scenario { get; set; } = "simple";
    public bool RunAllScenarios { get; set; }
    public bool PersistDatabases { get; set; }
    public int RecordCount { get; set; } = 10;
    public int BatchSize { get; set; } = 1000;
    public string ServerUrl { get; set; } = "https://localhost:5001";
    public bool Verbose { get; set; }
}
