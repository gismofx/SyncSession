namespace SyncSession.Core.Constants;

/// <summary>
/// Standard error messages used throughout the SyncSession library.
/// </summary>
public static class ErrorMessages
{
    // Session errors
    /// <summary>The requested session does not exist.</summary>
    public const string SESSION_NOT_FOUND = "Session not found";
    /// <summary>The operation cannot proceed because the session has already completed.</summary>
    public const string SESSION_ALREADY_COMPLETED = "Session already completed";
    /// <summary>The session exceeded its activity timeout and is no longer valid.</summary>
    public const string SESSION_EXPIRED = "Session has expired";
    /// <summary>The session is not in a state that allows the requested operation.</summary>
    public const string INVALID_SESSION_STATE = "Invalid session state";

    // Synchronization errors
    /// <summary>A sync operation is already in progress for this client; concurrent sync is not permitted.</summary>
    public const string CONCURRENT_SYNC_NOT_ALLOWED = "Concurrent synchronization not allowed for this client";
    /// <summary>The sync operation did not complete within the allowed time limit.</summary>
    public const string SYNC_TIMEOUT = "Synchronization operation timed out";
    /// <summary>The number of records in the batch exceeds the configured maximum batch size.</summary>
    public const string BATCH_SIZE_EXCEEDED = "Batch size exceeds maximum allowed";

    // Database errors
    /// <summary>A connection to the database could not be established.</summary>
    public const string DATABASE_CONNECTION_FAILED = "Failed to connect to database";
    /// <summary>The database transaction could not be committed or was rolled back.</summary>
    public const string TRANSACTION_FAILED = "Database transaction failed";
    /// <summary>The specified table does not exist in the database.</summary>
    public const string TABLE_NOT_FOUND = "Specified table not found";

    // Validation errors
    /// <summary>The client ID is missing or not a valid GUID.</summary>
    public const string INVALID_CLIENT_ID = "Invalid or missing client ID";
    /// <summary>The session ID is missing or not a valid GUID.</summary>
    public const string INVALID_SESSION_ID = "Invalid or missing session ID";
    /// <summary>The table name is missing, empty, or contains invalid characters.</summary>
    public const string INVALID_TABLE_NAME = "Invalid or missing table name";
    /// <summary>The record set provided to the operation is empty.</summary>
    public const string EMPTY_RECORD_SET = "Record set cannot be empty";

    // Configuration errors
    /// <summary>Required SyncSession configuration is absent.</summary>
    public const string CONFIGURATION_MISSING = "Synchronization configuration is missing";
    /// <summary>The specified table has not been registered for synchronization.</summary>
    public const string TABLE_NOT_CONFIGURED = "Table is not configured for synchronization";
}
