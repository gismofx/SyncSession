namespace SyncSession.Core.Constants;

/// <summary>
/// Standard error messages used throughout the system
/// </summary>
public static class ErrorMessages
{
    // Session errors
    public const string SESSION_NOT_FOUND = "Session not found";
    public const string SESSION_ALREADY_COMPLETED = "Session already completed";
    public const string SESSION_EXPIRED = "Session has expired";
    public const string INVALID_SESSION_STATE = "Invalid session state";
    
    // Synchronization errors
    public const string CONCURRENT_SYNC_NOT_ALLOWED = "Concurrent synchronization not allowed for this client";
    public const string SYNC_TIMEOUT = "Synchronization operation timed out";
    public const string BATCH_SIZE_EXCEEDED = "Batch size exceeds maximum allowed";
    
    // Database errors
    public const string DATABASE_CONNECTION_FAILED = "Failed to connect to database";
    public const string TRANSACTION_FAILED = "Database transaction failed";
    public const string TABLE_NOT_FOUND = "Specified table not found";
    
    // Validation errors
    public const string INVALID_CLIENT_ID = "Invalid or missing client ID";
    public const string INVALID_SESSION_ID = "Invalid or missing session ID";
    public const string INVALID_TABLE_NAME = "Invalid or missing table name";
    public const string EMPTY_RECORD_SET = "Record set cannot be empty";
    
    // Configuration errors
    public const string CONFIGURATION_MISSING = "Synchronization configuration is missing";
    public const string TABLE_NOT_CONFIGURED = "Table is not configured for synchronization";
}
