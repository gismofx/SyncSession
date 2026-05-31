namespace SyncSession.Core.Models;

/// <summary>
/// Represents the current phase of a synchronization operation.
/// </summary>
public enum SyncPhase
{
    /// <summary>
    /// Connecting to the server
    /// </summary>
    Connecting,

    /// <summary>
    /// Beginning push session
    /// </summary>
    PushBegin,

    /// <summary>
    /// Pushing a specific table
    /// </summary>
    PushTable,

    /// <summary>
    /// Completing push session
    /// </summary>
    PushComplete,

    /// <summary>
    /// Waiting for the server to commit the push session.
    /// </summary>
    PushWaiting,

    /// <summary>
    /// Beginning pull session
    /// </summary>
    PullBegin,

    /// <summary>
    /// Pulling a specific table
    /// </summary>
    PullTable,

    /// <summary>
    /// Completing pull session
    /// </summary>
    PullComplete,

    /// <summary>
    /// Synchronization complete
    /// </summary>
    Complete,

    /// <summary>
    /// Synchronization was cancelled via CancellationToken
    /// </summary>
    Cancelled
}
