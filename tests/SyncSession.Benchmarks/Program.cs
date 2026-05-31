using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace SyncSession.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        // Quick validation mode - test all benchmarks once
        if (args.Contains("--validate"))
        {
            Console.WriteLine("Validating all benchmarks...\n");
            
            var assembly = typeof(Program).Assembly;
            var benchmarkTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<MemoryDiagnoserAttribute>() != null)
                .ToList();
            
            var totalErrors = 0;
            
            foreach (var benchmarkType in benchmarkTypes)
            {
                Console.WriteLine($"▶ {benchmarkType.Name}");
                
                try
                {
                    var instance = Activator.CreateInstance(benchmarkType);
                    
                    // Run GlobalSetup
                    var globalSetup = benchmarkType.GetMethods()
                        .FirstOrDefault(m => m.GetCustomAttribute<GlobalSetupAttribute>() != null);
                    if (globalSetup != null)
                    {
                        Console.Write("    GlobalSetup... ");
                        InvokeMethod(globalSetup, instance);
                        Console.WriteLine("✓");
                    }
                    
                    // Get all benchmark methods
                    var benchmarkMethods = benchmarkType.GetMethods()
                        .Where(m => m.GetCustomAttribute<BenchmarkAttribute>() != null)
                        .ToList();
                    
                    foreach (var method in benchmarkMethods)
                    {
                        // Run IterationSetup if exists
                        var iterationSetup = benchmarkType.GetMethods()
                            .FirstOrDefault(m => m.GetCustomAttribute<IterationSetupAttribute>() != null);
                        if (iterationSetup != null)
                        {
                            InvokeMethod(iterationSetup, instance);
                        }
                        
                        // Run benchmark method
                        Console.Write($"    {method.Name}... ");
                        InvokeMethod(method, instance);
                        Console.WriteLine("✓");
                        
                        // Run IterationCleanup if exists
                        var iterationCleanup = benchmarkType.GetMethods()
                            .FirstOrDefault(m => m.GetCustomAttribute<IterationCleanupAttribute>() != null);
                        if (iterationCleanup != null)
                        {
                            InvokeMethod(iterationCleanup, instance);
                        }
                    }
                    
                    // Run GlobalCleanup
                    var globalCleanup = benchmarkType.GetMethods()
                        .FirstOrDefault(m => m.GetCustomAttribute<GlobalCleanupAttribute>() != null);
                    if (globalCleanup != null)
                    {
                        Console.Write("    GlobalCleanup... ");
                        InvokeMethod(globalCleanup, instance);
                        Console.WriteLine("✓");
                    }
                    
                    Console.WriteLine($"  ✓ {benchmarkType.Name} validated\n");
                }
                catch (Exception ex)
                {
                    totalErrors++;
                    Console.WriteLine($"\n  ✗ {benchmarkType.Name} FAILED");
                    Console.WriteLine($"    Error: {ex.InnerException?.Message ?? ex.Message}");
                    Console.WriteLine($"    {ex.InnerException?.StackTrace ?? ex.StackTrace}\n");
                }
            }
            
            Console.WriteLine("================================================================================");
            if (totalErrors == 0)
            {
                Console.WriteLine($"✓ All {benchmarkTypes.Count} benchmark classes validated successfully");
            }
            else
            {
                Console.WriteLine($"✗ {totalErrors}/{benchmarkTypes.Count} benchmark classes failed");
            }
            Console.WriteLine("================================================================================");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }

        // Check if running in analysis-only mode
        if (args.Length > 0 && args[0] == "--analyze")
        {
            var path = args.Length > 1 ? args[1] : FindLatestResults();
            if (path == null)
            {
                Console.WriteLine("No results found. Specify path: dotnet run --analyze <path>");
                return;
            }
            
            Console.WriteLine($"Analyzing results from: {path}");
            ResultsAnalyzer.AnalyzeResults(path);
            return;
        }

        var logPath = Path.Combine(Directory.GetCurrentDirectory(), "benchmark-error.log");
        
        try
        {
            var config = new BenchmarkConfig();
            var summary = BenchmarkSwitcher
                .FromAssembly(typeof(Program).Assembly)
                .Run(args, config);
            
            // Analyze results and create concise summary
            ResultsAnalyzer.AnalyzeResults(config.ArtifactsPath);
            
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine($"📁 Full results: {config.ArtifactsPath}");
            Console.WriteLine($"📊 Review: {Path.Combine(config.ArtifactsPath, "BASELINE-SUMMARY.md")}");
            Console.WriteLine("================================================================================");
            
            // Delete error log if successful
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
        catch (Exception ex)
        {
            var errorMessage = $"[{DateTime.UtcNow:O}] BENCHMARK ERROR:\n{ex}\n\n";
            
            // Write to file
            File.AppendAllText(logPath, errorMessage);
            
            // Display to console
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("BENCHMARK ERROR:");
            Console.WriteLine("================================================================================");
            Console.WriteLine(ex.ToString());
            Console.WriteLine();
            Console.WriteLine($"Error also saved to: {logPath}");
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
            Environment.Exit(1);
        }
    }
    
    private static void InvokeMethod(MethodInfo method, object? instance)
    {
        var result = method.Invoke(instance, null);
        
        // Handle async methods
        if (result is Task task)
        {
            task.GetAwaiter().GetResult();
        }
    }

    private static string? FindLatestResults()
    {
        var resultsDir = Path.Combine(Directory.GetCurrentDirectory(), "Results");
        if (!Directory.Exists(resultsDir)) return null;

        var dirs = Directory.GetDirectories(resultsDir)
            .OrderByDescending(d => Directory.GetCreationTime(d))
            .ToArray();

        return dirs.Length > 0 ? dirs[0] : null;
    }
}
