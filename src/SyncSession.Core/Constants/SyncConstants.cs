namespace SyncSession.Core.Constants;

/// <summary>
/// Core constants used throughout the SyncSession library.
/// </summary>
public static class SyncConstants
{
    // Default thresholds
    /// <summary>Record count above which a push operation uses a dedicated temp table instead of the shared table.</summary>
    public const int DEFAULT_PUSH_SHARED_TABLE_THRESHOLD = 10000;
    /// <summary>Record count above which a pull operation uses a dedicated temp table instead of the shared table.</summary>
    public const int DEFAULT_PULL_SHARED_TABLE_THRESHOLD = 10000;

    // Default batch sizes
    /// <summary>Default number of records per push batch.</summary>
    public const int DEFAULT_PUSH_BATCH_SIZE = 1000;
    /// <summary>Default number of records per pull batch.</summary>
    public const int DEFAULT_PULL_BATCH_SIZE = 1000;

    // Timeouts
    /// <summary>Minutes of inactivity after which a session is considered stale and marked Failed.</summary>
    public const int DEFAULT_SESSION_TIMEOUT_MINUTES = 30;
    /// <summary>Interval in minutes between cleanup service runs.</summary>
    public const int DEFAULT_CLEANUP_INTERVAL_MINUTES = 60;
    /// <summary>Age in days after which orphaned dedicated temp tables are eligible for cleanup.</summary>
    public const int DEFAULT_ORPHANED_TABLE_CLEANUP_DAYS = 1;

    // Keep-alive interval
    /// <summary>Interval in minutes at which long-running operations should call keep-alive to prevent session timeout.</summary>
    public const int KEEPALIVE_INTERVAL_MINUTES = 5;

    // Retry settings
    /// <summary>Default maximum number of retry attempts for transient failures.</summary>
    public const int DEFAULT_MAX_RETRIES = 3;
    /// <summary>Default base delay in milliseconds between retry attempts (exponential back-off base).</summary>
    public const int DEFAULT_BASE_DELAY_MS = 1000;

    // Session status
    /// <summary>Session is being staged; records are being uploaded to temp tables.</summary>
    public const string STATUS_STAGING = "Staging";
    /// <summary>All records uploaded; session is queued for background processing.</summary>
    public const string STATUS_READY = "Ready";
    /// <summary>Background processor is actively committing records from temp tables.</summary>
    public const string STATUS_PROCESSING = "Processing";
    /// <summary>Session has been successfully committed and assigned a SyncVersion.</summary>
    public const string STATUS_COMMITTED = "Committed";
    /// <summary>Session encountered an unrecoverable error and was not committed.</summary>
    public const string STATUS_FAILED = "Failed";
    /// <summary>Pull session completed and client processed sessions have been recorded.</summary>
    public const string STATUS_COMPLETED = "Completed";
    /// <summary>Session was cancelled before completion.</summary>
    public const string STATUS_CANCELLED = "Cancelled";
    /// <summary>Pull session is actively transferring records to the client.</summary>
    public const string STATUS_PULLING = "Pulling";

    // Log levels (Serilog level strings — intentionally not using SyncConstants in log calls)
    /// <summary>Serilog informational level string.</summary>
    public const string LOG_LEVEL_INFO = "Info";
    /// <summary>Serilog warning level string.</summary>
    public const string LOG_LEVEL_WARNING = "Warning";
    /// <summary>Serilog error level string.</summary>
    public const string LOG_LEVEL_ERROR = "Error";
    /// <summary>Serilog debug level string.</summary>
    public const string LOG_LEVEL_DEBUG = "Debug";

    // Session types
    /// <summary>Session type for client-to-server record uploads.</summary>
    public const string SESSION_TYPE_PUSH = "Push";
    /// <summary>Session type for server-to-client record downloads.</summary>
    public const string SESSION_TYPE_PULL = "Pull";
    /// <summary>Session type for initial full-dataset seed operations.</summary>
    public const string SESSION_TYPE_SEED = "Seed";
    /// <summary>Session type for direct server-side write operations bypassing the temp table queue.</summary>
    public const string SESSION_TYPE_DIRECT = "DirectWrite";
}
