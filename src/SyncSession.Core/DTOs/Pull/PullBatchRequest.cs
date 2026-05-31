using System;
using System.ComponentModel.DataAnnotations;
using SyncSession.Core.Validation;

namespace SyncSession.Core.DTOs.Pull;

/// <summary>
/// Query parameters for retrieving a paginated batch of records from a pull session temp table.
/// </summary>
/// <remarks>
/// Bound from the query string via <c>[FromQuery]</c> on the pull/batch GET endpoint.
/// <c>Limit</c> has no enforced upper bound here — the effective maximum is governed by
/// <c>SyncConfiguration.PullBatchSize</c> on the server.
/// </remarks>
public class PullBatchRequest
{
    /// <summary>Gets or sets the pull session ID issued by the server during pull/begin.</summary>
    [RequiredGuid]
    public Guid PullSessionId { get; set; }

    /// <summary>Gets or sets the business table name to retrieve records from (e.g., <c>Customers</c>).</summary>
    [Required]
    public string TableName { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of records to skip for pagination.</summary>
    [Range(0, int.MaxValue)]
    public int Offset { get; set; }

    /// <summary>Gets or sets the maximum number of records to return in this batch.</summary>
    [Range(1, int.MaxValue)]
    public int Limit { get; set; } = 1000;
}
