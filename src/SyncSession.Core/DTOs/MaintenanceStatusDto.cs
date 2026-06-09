namespace SyncSession.Core.DTOs;

/// <summary>
/// Response shape for admin maintenance status endpoints.
/// Returned by <c>GET/POST /api/v1/admin/maintenance</c>.
/// </summary>
public class MaintenanceStatusDto
{
    /// <summary>Whether maintenance mode is currently active.</summary>
    public bool MaintenanceEnabled { get; set; }

    /// <summary>
    /// Number of sessions currently in progress — Status NOT IN
    /// (Committed, Failed, Completed, Cancelled).
    /// </summary>
    public int ActiveSessionCount { get; set; }

    /// <summary>Number of sessions queued for background processing (Status = Ready).</summary>
    public int QueueDepth { get; set; }

    /// <summary>
    /// Whether the server is safe for maintenance: <c>true</c> when maintenance mode
    /// is enabled and both <see cref="ActiveSessionCount"/> and <see cref="QueueDepth"/>
    /// are zero.
    /// </summary>
    public bool ReadyForMaintenance { get; set; }
}
