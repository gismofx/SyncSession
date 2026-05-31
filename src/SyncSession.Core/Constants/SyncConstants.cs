namespace SyncSession.Core.Constants;

/// <summary>
/// Core constants used throughout the SyncSystem
/// </summary>
public static class SyncConstants
{
    // Default thresholds
    public const int DEFAULT_PUSH_SHARED_TABLE_THRESHOLD = 10000;
    public const int DEFAULT_PULL_SHARED_TABLE_THRESHOLD = 10000;

    // Default batch sizes
    public const int DEFAULT_PUSH_BATCH_SIZE = 1000;
    public const int DEFAULT_PULL_BATCH_SIZE = 1000;

    // Timeouts
    public const int DEFAULT_SESSION_TIMEOUT_MINUTES = 30;
    public const int DEFAULT_CLEANUP_INTERVAL_MINUTES = 60;
    public const int DEFAULT_ORPHANED_TABLE_CLEANUP_DAYS = 1;

    // Keep-alive interval
    public const int KEEPALIVE_INTERVAL_MINUTES = 5;

    // Retry settings
    public const int DEFAULT_MAX_RETRIES = 3;
    public const int DEFAULT_BASE_DELAY_MS = 1000;

    // Session status
    public const string STATUS_STAGING = "Staging";
    public const string STATUS_READY = "Ready";
    public const string STATUS_PROCESSING = "Processing";
    public const string STATUS_COMMITTED = "Committed";
    public const string STATUS_FAILED = "Failed";
    public const string STATUS_COMPLETED = "Completed";
    public const string STATUS_CANCELLED = "Cancelled";
    public const string STATUS_PULLING = "Pulling";

    // Log levels
    public const string LOG_LEVEL_INFO = "Info";
    public const string LOG_LEVEL_WARNING = "Warning";
    public const string LOG_LEVEL_ERROR = "Error";
    public const string LOG_LEVEL_DEBUG = "Debug";
    
    // Session types
    public const string SESSION_TYPE_PUSH = "Push";
    public const string SESSION_TYPE_PULL = "Pull";
    public const string SESSION_TYPE_SEED = "Seed";
    public const string SESSION_TYPE_DIRECT = "DirectWrite";
}
