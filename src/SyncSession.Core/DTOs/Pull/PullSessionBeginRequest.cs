using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using SyncSession.Core.Validation;

namespace SyncSession.Core.DTOs.Pull;

/// <summary>
/// Request to begin a new pull session.
/// </summary>
public class PullSessionBeginRequest
{
    /// <summary>Gets or sets the device ID initiating the pull.</summary>
    [RequiredGuid]
    public Guid DeviceId { get; set; }

    /// <summary>Gets or sets the tenant ID scoping this session, or <c>null</c> for non-tenant deployments.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Gets or sets the business table names to pull from.</summary>
    [Required]
    public IEnumerable<string> TableNames { get; set; } = null!;

    /// <summary>Gets or sets an optional version floor — only sessions after this version are returned.</summary>
    /// <value>A sync version to filter from, or <c>null</c> to pull all unseen sessions.</value>
    public long? AfterVersion { get; set; }

    /// <summary>Optional human-readable display name for audit logging (38l).</summary>
    public string? UserDisplayName { get; set; }
}
