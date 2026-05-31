using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using SyncSession.Core.Validation;

namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Request to begin a new push session.
/// </summary>
public class PushSessionBeginRequest
{
    /// <summary>Gets or sets the device ID initiating the push.</summary>
    [RequiredGuid]
    public Guid DeviceId { get; set; }

    /// <summary>Gets or sets the tenant ID scoping this session, or <c>null</c> for non-tenant deployments.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Gets or sets the tables to be pushed with their estimated record counts.</summary>
    [Required]
    public IEnumerable<TableSyncInfo> Tables { get; set; } = null!;

    /// <summary>Optional human-readable display name for audit logging (38l).</summary>
    public string? UserDisplayName { get; set; }
}


