using System;

namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Response for push session status polling.
/// </summary>
public class PushSessionStatusResponse
{
    /// <summary>Gets or sets the session ID being polled.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the current session status.</summary>
    /// <value>One of: <c>Staging</c>, <c>Ready</c>, <c>Processing</c>, <c>Committed</c>, <c>Failed</c>.</value>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the sync version assigned when the session committed.</summary>
    /// <value>The version number, or <c>null</c> if the session has not yet committed.</value>
    public long? SyncVersion { get; set; }

    /// <summary>Gets or sets the error message when the session fails.</summary>
    /// <value>The error description, or <c>null</c> if the session has not failed.</value>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the session was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the most recent session activity.</summary>
    public DateTime LastActivityUtc { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the session was committed.</summary>
    /// <value>The commit time, or <c>null</c> if the session has not yet committed.</value>
    public DateTime? CommittedAtUtc { get; set; }
}
