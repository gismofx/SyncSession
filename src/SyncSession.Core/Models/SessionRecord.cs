using System;
using System.ComponentModel.DataAnnotations;
using SyncSession.Core.Validation;

namespace SyncSession.Core.Models;

/// <summary>
/// Represents a synchronization session (Push or Pull)
/// </summary>
public class SessionRecord
{
    /// <summary>
    /// Unique identifier for this session
    /// </summary>
    [RequiredGuid]
    public Guid SessionId { get; set; }

    /// <summary>
    /// The tenant scoping this session, or <c>null</c> for non-tenant deployments.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The device that initiated this session (38l).
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Authenticated user ID from token claims. Null if unavailable (38l).
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Human-readable display name from the token claim configured in
    /// <c>SyncSystemOptions.DisplayNameClaimType</c>. Null if unavailable (38l).
    /// </summary>
    public string? UserDisplayName { get; set; }

    /// <summary>
    /// Type of session: "Push", "Pull", or "Seed"
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string SessionType { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the session
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The sync version assigned to this session (once committed)
    /// </summary>
    public long? SyncVersion { get; set; }

    /// <summary>
    /// When the session was created (UTC)
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Last activity timestamp for timeout detection (UTC)
    /// </summary>
    public DateTime LastActivityUtc { get; set; }

    /// <summary>
    /// When the session was committed (UTC)
    /// </summary>
    public DateTime? CommittedAtUtc { get; set; }

    /// <summary>
    /// Error message if session failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Total records synced or seeded across all tables (38l).
    /// Set at commit time (push), completion (pull), or acknowledge (seed).
    /// </summary>
    public int TotalRows { get; set; }

    /// <summary>
    /// Per-table row count breakdown as JSON: {"TableA":100,"TableB":200} (38l).
    /// Null if unavailable.
    /// </summary>
    public string? RowCountsJson { get; set; }
}
