using System;
using System.Collections.Generic;

namespace SyncSession.Core.DTOs.Pull;

/// <summary>
/// Represents the server's response to a pull session begin request.
/// </summary>
public class PullSessionBeginResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the pull session was created successfully.
    /// </summary>
    /// <value><c>true</c> if the session was created; <c>false</c> otherwise.</value>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the assigned pull session ID.
    /// </summary>
    /// <value>The pull session ID to use in subsequent batch and complete calls.</value>
    public Guid PullSessionId { get; set; }

    /// <summary>
    /// Gets or sets the temp table metadata keyed by business table name.
    /// </summary>
    /// <value>A dictionary mapping table names to their record counts and temp table configuration.</value>
    public Dictionary<string, SyncSessionTableMetadata> Tables { get; set; } = new();

    /// <summary>
    /// Gets or sets the error message when the request fails.
    /// </summary>
    /// <value>The error description, or <c>null</c> if the request succeeded.</value>
    public string? ErrorMessage { get; set; }
}
