using System;
using System.Collections.Generic;

namespace SyncSession.Core.DTOs;

/// <summary>
/// Summary of a committed sync session for audit and monitoring purposes.
/// </summary>
public class SyncSessionSummary
{
    /// <summary>Gets or sets the unique session identifier.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the tenant ID scoping this session, or <c>null</c> for non-tenant sessions.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Gets or sets the device that initiated this session, or <c>null</c> if unavailable.</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>Gets or sets the session type: <c>Push</c> or <c>Pull</c>.</summary>
    public string SessionType { get; set; } = string.Empty;

    /// <summary>Gets or sets the committed sync version assigned by the server.</summary>
    public long SyncVersion { get; set; }

    /// <summary>Gets or sets the UTC time the session was committed.</summary>
    public DateTime CommittedAtUtc { get; set; }

    /// <summary>Gets or sets the tables touched in this session with their record counts.</summary>
    public IReadOnlyList<SyncSessionTableSummary> Tables { get; set; } = Array.Empty<SyncSessionTableSummary>();
}

/// <summary>
/// Per-table record count within a <see cref="SyncSessionSummary"/>.
/// </summary>
public class SyncSessionTableSummary
{
    /// <summary>Gets or sets the business table name.</summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>Gets or sets the actual number of records processed in this session for this table.</summary>
    public int RecordCount { get; set; }
}
