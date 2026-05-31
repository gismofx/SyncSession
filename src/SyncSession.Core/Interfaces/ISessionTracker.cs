using System;
using System.Threading.Tasks;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Manages sync session lifecycle and state for both push and pull operations.
/// </summary>
public interface ISessionTracker
{
    /// <summary>
    /// Creates a new push session, assigns temp tables, and tracks session tables.
    /// </summary>
    Task<PushSessionBeginResponse> CreatePushSessionAsync(
        PushSessionBeginRequest request, string? userId = null, string? userDisplayName = null);

    /// <summary>
    /// Creates a new pull session, snapshots unseen records into temp tables.
    /// Returns an empty response (no session created) if no unseen sessions exist.
    /// </summary>
    Task<PullSessionBeginResponse> CreatePullSessionAsync(
        PullSessionBeginRequest request, string? userId = null, string? userDisplayName = null);

    /// <summary>
    /// Returns true if a session exists, optionally matching the expected status.
    /// </summary>
    Task<bool> SessionExistsAsync(Guid sessionId, string? expectedStatus = null);

    /// <summary>
    /// Updates the session's LastActivityUtc timestamp (keep-alive).
    /// </summary>
    Task UpdateSessionActivityAsync(Guid sessionId);

    /// <summary>
    /// Verifies record counts for a completed table and marks it Ready.
    /// </summary>
    Task<PushTableCompleteResponse> CompleteTableAsync(Guid sessionId, string tableName, int totalRecordsSent);

    /// <summary>
    /// Marks a session as Ready for background queue processing.
    /// </summary>
    Task<bool> MarkSessionReadyAsync(Guid sessionId);

    /// <summary>
    /// Returns the current status of a push session for client polling.
    /// Returns null if the session does not exist.
    /// </summary>
    Task<PushSessionStatusResponse?> GetSessionStatusAsync(Guid sessionId);

    /// <summary>
    /// Updates the pull session's LastActivityUtc timestamp (keep-alive).
    /// </summary>
    Task UpdatePullSessionActivityAsync(Guid pullSessionId);
}
