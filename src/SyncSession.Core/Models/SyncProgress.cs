namespace SyncSession.Core.Models;

/// <summary>
/// Progress information for a synchronization operation
/// </summary>
public class SyncProgress
{
    /// <summary>
    /// Current phase of synchronization
    /// </summary>
    public SyncPhase Phase { get; set; }

    /// <summary>
    /// Name of the table currently being synchronized (null if not in table phase)
    /// </summary>
    public string? CurrentTable { get; set; }

    /// <summary>
    /// Number of tables completed so far in the current sync operation (push + pull combined).
    /// Starts at 0, reaches TotalTables when sync is complete.
    /// </summary>
    public int TablesCompleted { get; set; }

    /// <summary>
    /// Total number of tables to synchronize
    /// </summary>
    public int TotalTables { get; set; }

    /// <summary>
    /// Number of records processed for the current table
    /// </summary>
    public long RecordsProcessed { get; set; }

    /// <summary>
    /// Total number of records for the current table
    /// </summary>
    public long TotalRecords { get; set; }

    /// <summary>
    /// Percentage complete for the current table (0-100)
    /// </summary>
    public double TablePercent => TotalRecords > 0
        ? (RecordsProcessed * 100.0 / TotalRecords)
        : 0;

    /// <summary>
    /// Overall percentage complete across all tables (0-100).
    /// Interpolates within the current table using TablePercent for smooth progress reporting.
    /// Formula: (TablesCompleted + TablePercent/100) / TotalTables * 100
    /// </summary>
    public double OverallPercent => TotalTables > 0
        ? ((TablesCompleted + TablePercent / 100.0) / TotalTables * 100.0)
        : 0;

    /// <summary>
    /// Non-fatal warning message for the current operation (null = no warning).
    /// Transient — only set on the report that carries the warning.
    /// Fatal errors are reported via SyncResult.ErrorMessage after the operation completes.
    /// </summary>
    public string? WarningMessage { get; set; }

    /// <summary>
    /// Human-readable status message describing current progress.
    /// When set explicitly, overrides the default phase-based message.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage ?? DefaultStatusMessage;
        set => _statusMessage = value;
    }
    private string? _statusMessage;

    private string DefaultStatusMessage => Phase switch
    {
        SyncPhase.Connecting  => "Connecting to server...",
        SyncPhase.PushBegin   => "Starting push session...",
        SyncPhase.PushTable   => $"Pushing {CurrentTable} ({RecordsProcessed:N0}/{TotalRecords:N0})",
        SyncPhase.PushComplete => "Finalizing push...",
        SyncPhase.PushWaiting => "Waiting for server to commit...",
        SyncPhase.PullBegin   => "Starting pull session...",
        SyncPhase.PullTable   => $"Pulling {CurrentTable} ({RecordsProcessed:N0}/{TotalRecords:N0})",
        SyncPhase.PullComplete => "Finalizing pull...",
        SyncPhase.Complete    => "Sync complete!",
        SyncPhase.Cancelled   => "Sync cancelled.",
        _ => "Syncing..."
    };
}
