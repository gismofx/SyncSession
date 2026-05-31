using System;
using System.Collections.Generic;

namespace SyncSession.Core.Models;

/// <summary>
/// Result of a synchronization operation
/// </summary>
public class SyncResult
{
    /// <summary>
    /// Whether the synchronization was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Total records pushed to server
    /// </summary>
    public int RecordsPushed { get; set; }

    /// <summary>
    /// Total records pulled from server
    /// </summary>
    public int RecordsPulled { get; set; }

    /// <summary>
    /// Records pushed per table
    /// </summary>
    public Dictionary<string, int> PushedPerTable { get; set; } = new();

    /// <summary>
    /// Records pulled per table
    /// </summary>
    public Dictionary<string, int> PulledPerTable { get; set; } = new();

    /// <summary>
    /// The final sync version after pull
    /// </summary>
    public long? FinalVersion { get; set; }

    /// <summary>
    /// Number of conflicts detected during sync
    /// </summary>
    public int ConflictsDetected { get; set; }

    /// <summary>
    /// Duration of the synchronization
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Error message if synchronization failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When the synchronization started (UTC)
    /// </summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// When the synchronization completed (UTC)
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }
}
