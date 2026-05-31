using System;
using System.ComponentModel.DataAnnotations;
using SyncSession.Core.Validation;

namespace SyncSession.Core.Models;

/// <summary>
/// Represents a table being synchronized within a session
/// </summary>
public class SyncSessionTable
{
    /// <summary>
    /// The session this table belongs to
    /// </summary>
    [RequiredGuid]
    public Guid SessionId { get; set; }

    /// <summary>
    /// Name of the table being synchronized
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the temp table used for this table
    /// </summary>
    [MaxLength(255)]
    public string? TempTableName { get; set; }

    /// <summary>
    /// Whether this uses a shared temp table (true) or dedicated temp table (false)
    /// </summary>
    public bool UsesSharedTable { get; set; }

    /// <summary>
    /// Current status of this table
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Estimated number of records (from client)
    /// </summary>
    public int EstimatedRecordCount { get; set; }

    /// <summary>
    /// Actual number of records received/sent
    /// </summary>
    public int ActualRecordCount { get; set; }

    /// <summary>
    /// Processing priority for this table (lower numbers processed first)
    /// </summary>
    public int ProcessingPriority { get; set; }
}
