using System;
using System.ComponentModel.DataAnnotations;
using SyncSession.Core.Validation;

namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Request to mark a table as fully uploaded within a push session.
/// </summary>
public class PushTableCompleteRequest
{
    /// <summary>Gets or sets the session ID this table belongs to.</summary>
    [RequiredGuid]
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the business table name being completed (e.g., <c>Customers</c>).</summary>
    [Required]
    public string TableName { get; set; } = string.Empty;

    /// <summary>Gets or sets the total number of records sent for this table across all batches.</summary>
    /// <remarks>The server compares this against its actual received count to detect upload errors.</remarks>
    [Range(0, int.MaxValue)]
    public int TotalRecordsSent { get; set; }
}
