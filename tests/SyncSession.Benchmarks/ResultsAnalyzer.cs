using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SyncSession.Benchmarks;

/// <summary>
/// Analyzes BenchmarkDotNet results and creates concise summary for review.
/// Reduces token consumption by extracting only key metrics and insights.
/// </summary>
public class ResultsAnalyzer
{
    public static void AnalyzeResults(string artifactsPath)
    {
        try
        {
            // Find the most recent results JSON
            var resultsFiles = Directory.GetFiles(artifactsPath, "*-report.json", SearchOption.AllDirectories);
            if (resultsFiles.Length == 0)
            {
                Console.WriteLine("No results JSON found.");
                return;
            }

            Console.WriteLine($"Found {resultsFiles.Length} result files");
            
            // Combine all benchmarks from all files
            var allBenchmarks = new List<JsonElement>();
            
            foreach (var file in resultsFiles)
            {
                Console.WriteLine($"Reading: {Path.GetFileName(file)}");
                var json = File.ReadAllText(file);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("Benchmarks", out var benchmarksElement))
                {
                    var fileBenchmarks = benchmarksElement.EnumerateArray().ToList();
                    allBenchmarks.AddRange(fileBenchmarks);
                    Console.WriteLine($"  - {fileBenchmarks.Count} benchmarks");
                }
            }

            Console.WriteLine($"Total benchmarks: {allBenchmarks.Count}");
            var benchmarks = allBenchmarks;

            var summary = new List<string>();
            summary.Add("# SyncSystem Benchmark Baseline Summary");
            summary.Add($"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            summary.Add($"**Total Benchmarks:** {benchmarks.Count}");
            summary.Add("");
            summary.Add("---");
            summary.Add("");

            // Group by class
            var byClass = benchmarks.GroupBy(b => 
                b.GetProperty("Type").GetString()?.Split('.').Last() ?? "Unknown");

            foreach (var group in byClass.OrderBy(g => g.Key))
            {
                summary.Add($"## {group.Key}");
                summary.Add("");

                var results = group.Select(b => new BenchmarkResult
                {
                    Method = b.GetProperty("Method").GetString() ?? "Unknown",
                    Parameters = GetParameters(b),
                    Mean = GetMetric(b, "Mean"),
                    Median = GetMetric(b, "Median"),
                    StdDev = GetMetric(b, "StdDev"),
                    Allocated = GetMemoryMetric(b, "Allocated"),
                    Gen0 = GetMemoryMetric(b, "Gen0"),
                    Gen1 = GetMemoryMetric(b, "Gen1")
                }).ToList();

                // Show top results by category
                if (group.Key.Contains("EntityReflection"))
                {
                    SummarizeEntityReflection(summary, results);
                }
                else if (group.Key.Contains("Json"))
                {
                    SummarizeJson(summary, results);
                }
                else if (group.Key.Contains("Database"))
                {
                    SummarizeDatabase(summary, results);
                }
                else
                {
                    // Generic summary
                    foreach (var r in results.OrderByDescending(r => r.Mean).Take(5))
                    {
                        summary.Add($"- **{r.Method}** {r.Parameters}");
                        summary.Add($"  - Mean: {FormatTime(r.Mean)}, Allocated: {FormatBytes(r.Allocated)}");
                    }
                }

                summary.Add("");
            }

            // Optimization targets
            summary.Add("---");
            summary.Add("");
            summary.Add("## 🎯 Optimization Targets for Session 18b");
            summary.Add("");

            var allResults = benchmarks.Select(b => new BenchmarkResult
            {
                Class = b.GetProperty("Type").GetString()?.Split('.').Last() ?? "Unknown",
                Method = b.GetProperty("Method").GetString() ?? "Unknown",
                Parameters = GetParameters(b),
                Mean = GetMetric(b, "Mean"),
                Allocated = GetMemoryMetric(b, "Allocated")
            }).ToList();

            // Top 5 slowest
            summary.Add("### Slowest Operations");
            foreach (var r in allResults.OrderByDescending(r => r.Mean).Take(5))
            {
                summary.Add($"1. **{r.Class}.{r.Method}** {r.Parameters} - {FormatTime(r.Mean)}");
            }
            summary.Add("");

            // Top 5 memory hogs
            summary.Add("### Highest Memory Allocations");
            foreach (var r in allResults.OrderByDescending(r => r.Allocated).Take(5))
            {
                summary.Add($"1. **{r.Class}.{r.Method}** {r.Parameters} - {FormatBytes(r.Allocated)}");
            }
            summary.Add("");

            // Write summary
            var summaryPath = Path.Combine(artifactsPath, "BASELINE-SUMMARY.md");
            File.WriteAllLines(summaryPath, summary);

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine($"📊 Baseline summary: {summaryPath}");
            Console.WriteLine("================================================================================");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error analyzing results: {ex.Message}");
        }
    }

    private static void SummarizeEntityReflection(List<string> summary, List<BenchmarkResult> results)
    {
        summary.Add("### Key Metrics");
        
        // Column extraction (cached)
        var cached = results.Where(r => r.Method.Contains("GetColumns") && r.Method.Contains("Cached") && !r.Method.Contains("Cold")).ToList();
        if (cached.Any())
        {
            var avg = cached.Average(r => r.Mean);
            summary.Add($"- **Column Extraction (Cached):** {FormatTime(avg)} avg, {FormatBytes((long)cached.Average(r => r.Allocated))} allocated");
        }

        // Cold cache
        var cold = results.FirstOrDefault(r => r.Method.Contains("ColdCache"));
        if (cold != null)
        {
            summary.Add($"- **Cold Cache Penalty:** {FormatTime(cold.Mean)} (vs {FormatTime(cached.Average(r => r.Mean))} cached)");
        }

        // CreateDynamicParameters
        var dynParams = results.Where(r => r.Method.Contains("CreateDynamicParameters")).OrderBy(r => r.Parameters).ToList();
        if (dynParams.Any())
        {
            summary.Add($"- **CreateDynamicParameters:**");
            foreach (var dp in dynParams)
            {
                summary.Add($"  - {dp.Parameters}: {FormatTime(dp.Mean)}, {FormatBytes(dp.Allocated)}");
            }
        }

        // Dictionary conversions
        var toDict = results.Where(r => r.Method.Contains("EntityToDictionary")).OrderBy(r => r.Parameters).ToList();
        var fromDict = results.Where(r => r.Method.Contains("DictionaryToEntity")).OrderBy(r => r.Parameters).ToList();
        if (toDict.Any() || fromDict.Any())
        {
            summary.Add($"- **Dictionary Conversions:**");
            if (toDict.Any())
            {
                foreach (var t in toDict)
                    summary.Add($"  - To Dictionary {t.Parameters}: {FormatTime(t.Mean)}, {FormatBytes(t.Allocated)}");
            }
            if (fromDict.Any())
            {
                foreach (var f in fromDict)
                    summary.Add($"  - From Dictionary {f.Parameters}: {FormatTime(f.Mean)}, {FormatBytes(f.Allocated)}");
            }
        }
    }

    private static void SummarizeJson(List<string> summary, List<BenchmarkResult> results)
    {
        summary.Add("### Serialization Performance");
        
        var serialize = results.Where(r => r.Method.Contains("Serialize") && !r.Method.Contains("Deserialize")).OrderBy(r => r.Parameters).ToList();
        var deserialize = results.Where(r => r.Method.Contains("Deserialize")).OrderBy(r => r.Parameters).ToList();
        var unwrap = results.Where(r => r.Method.Contains("Unwrap")).OrderBy(r => r.Parameters).ToList();

        if (serialize.Any())
        {
            summary.Add("**Serialize:**");
            foreach (var s in serialize.Take(3))
                summary.Add($"- {s.Method} {s.Parameters}: {FormatTime(s.Mean)}");
        }

        if (deserialize.Any())
        {
            summary.Add("**Deserialize:**");
            foreach (var d in deserialize.Take(3))
                summary.Add($"- {d.Method} {d.Parameters}: {FormatTime(d.Mean)}");
        }

        if (unwrap.Any())
        {
            summary.Add("**UnwrapJsonElements strategy comparison:**");
            foreach (var u in unwrap)
                summary.Add($"- {u.Method} {u.Parameters}: {FormatTime(u.Mean)}, {FormatBytes(u.Allocated)}");
        }
    }

    private static void SummarizeDatabase(List<string> summary, List<BenchmarkResult> results)
    {
        summary.Add("### Database Operations");
        
        foreach (var r in results.OrderBy(r => r.Parameters).ThenBy(r => r.Method))
        {
            summary.Add($"- **{r.Method}** {r.Parameters}: {FormatTime(r.Mean)}");
        }
    }

    private static string GetParameters(JsonElement benchmark)
    {
        try
        {
            if (benchmark.TryGetProperty("Parameters", out var paramsObj))
            {
                // Handle both string and object formats
                if (paramsObj.ValueKind == JsonValueKind.String)
                {
                    return paramsObj.GetString() ?? "";
                }
                
                if (paramsObj.ValueKind == JsonValueKind.Object)
                {
                    var items = paramsObj.EnumerateObject()
                        .Select(p => $"{p.Name}={p.Value}")
                        .ToList();
                    return items.Any() ? $"({string.Join(", ", items)})" : "";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Error parsing parameters: {ex.Message}");
        }
        return "";
    }

    private static double GetMetric(JsonElement benchmark, string metricName)
    {
        try
        {
            if (benchmark.TryGetProperty("Statistics", out var stats))
            {
                if (stats.TryGetProperty(metricName, out var metric))
                {
                    return metric.GetDouble();
                }
            }
        }
        catch { }
        return 0;
    }

    private static long GetMemoryMetric(JsonElement benchmark, string metricName)
    {
        try
        {
            if (benchmark.TryGetProperty("Memory", out var memory))
            {
                // BenchmarkDotNet JSON uses "BytesAllocatedPerOperation" not "Allocated"
                var key = metricName == "Allocated" ? "BytesAllocatedPerOperation" : metricName;
                if (memory.TryGetProperty(key, out var metric))
                {
                    return metric.GetInt64();
                }
            }
        }
        catch { }
        return 0;
    }

    private static string FormatTime(double nanoseconds)
    {
        if (nanoseconds < 1000) return $"{nanoseconds:F1} ns";
        if (nanoseconds < 1_000_000) return $"{nanoseconds / 1000:F1} μs";
        if (nanoseconds < 1_000_000_000) return $"{nanoseconds / 1_000_000:F1} ms";
        return $"{nanoseconds / 1_000_000_000:F2} s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }

    private class BenchmarkResult
    {
        public string Class { get; set; } = "";
        public string Method { get; set; } = "";
        public string Parameters { get; set; } = "";
        public double Mean { get; set; }
        public double Median { get; set; }
        public double StdDev { get; set; }
        public long Allocated { get; set; }
        public long Gen0 { get; set; }
        public long Gen1 { get; set; }
    }
}
