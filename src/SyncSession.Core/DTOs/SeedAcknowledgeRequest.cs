using System;
using SyncSession.Core.Validation;

namespace SyncSession.Core.DTOs;

/// <summary>
/// Request to acknowledge that a seed operation has completed.
/// Marks all currently-committed sessions as processed for the device so that
/// subsequent pulls return only post-seed delta records.
/// Also carries activity log data written to SyncActivityLog by the server.
/// </summary>
public class SeedAcknowledgeRequest
{
    /// <summary>Gets or sets the device ID that completed the seed.</summary>
    [RequiredGuid]
    public Guid DeviceId { get; set; }

    /// <summary>Gets or sets the tenant that was seeded. Only sessions for this tenant are marked processed.</summary>
    [RequiredGuid]
    public Guid TenantId { get; set; }

    // ── Activity log fields ───────────────────────────────────────────────────

    /// <summary>Terminal status: 'Complete', 'Cancelled', or 'Failed'.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC time the seed operation started on the client.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Total rows written across all tables.</summary>
    public int TotalRows { get; set; }

    /// <summary>JSON object mapping table name to row count. Null if unavailable.</summary>
    public string? RowCountsJson { get; set; }

    /// <summary>Error detail for Failed/Cancelled status. Null on success.</summary>
    public string? ErrorDetail { get; set; }

    /// <summary>Authenticated user ID from client token claims. Null if unavailable.</summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Human-readable display name from the client's token claim.
    /// Null if the configured claim is unavailable.
    /// </summary>
    public string? UserDisplayName { get; set; }
}
