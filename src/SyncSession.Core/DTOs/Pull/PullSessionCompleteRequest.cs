using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using SyncSession.Core.Validation;

namespace SyncSession.Core.DTOs.Pull;

/// <summary>
/// Represents a request to complete a pull session and mark processed sessions.
/// </summary>
public class PullSessionCompleteRequest
{
    /// <summary>
    /// Gets or sets the pull session ID issued by the server during pull begin.
    /// </summary>
    /// <value>The pull session ID that identifies the temp data to clean up server-side.</value>
    [RequiredGuid]
    public Guid PullSessionId { get; set; }

    /// <summary>
    /// Gets or sets the device ID of the device completing the pull.
    /// </summary>
    /// <value>The unique identifier for the physical device, used to track processed sessions per device.</value>
    [RequiredGuid]
    public Guid DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the session IDs that were processed during this pull.
    /// </summary>
    [Required]
    public IEnumerable<Guid> ProcessedSessionIds { get; set; } = null!;

    /// <summary>
    /// Gets or sets the temp table metadata received from the pull session begin response.
    /// </summary>
    /// <value>A dictionary mapping table names to their temp table configuration, used for server-side cleanup.</value>
    [Required]
    public Dictionary<string, SyncSessionTableMetadata> Tables { get; set; } = new();
}
