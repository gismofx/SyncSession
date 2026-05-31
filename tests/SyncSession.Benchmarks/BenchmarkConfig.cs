using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace SyncSession.Benchmarks;

/// <summary>
/// Custom BenchmarkDotNet configuration for SyncSystem performance tracking.
/// Exports results in both JSON (machine-readable) and Markdown (human-readable) formats.
/// </summary>
public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        // Export results to Results folder
        var artifactsPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Results",
            $"{DateTime.UtcNow:yyyy-MM-dd-HHmmss}"
        );

        ArtifactsPath = artifactsPath;

        // Add exporters
        AddExporter(MarkdownExporter.GitHub);  // GitHub-flavored markdown
        AddExporter(new JsonExporter(
            indentJson: true,
            excludeMeasurements: false
        ));
        AddExporter(HtmlExporter.Default);     // HTML report
        
        // Add loggers
        AddLogger(ConsoleLogger.Default);

        // Configure columns to display
        AddColumn(StatisticColumn.Mean);
        AddColumn(StatisticColumn.Median);
        AddColumn(StatisticColumn.StdDev);
        AddColumn(StatisticColumn.Min);
        AddColumn(StatisticColumn.Max);
        AddColumn(StatisticColumn.P95);
        AddColumn(BaselineRatioColumn.RatioMean);
        
        // Job configuration - single run for baseline
        AddJob(Job.Default
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(10)
        );

        // Summary style
        WithSummaryStyle(BenchmarkDotNet.Reports.SummaryStyle.Default
            .WithRatioStyle(BenchmarkDotNet.Columns.RatioStyle.Trend)
        );
    }
}
