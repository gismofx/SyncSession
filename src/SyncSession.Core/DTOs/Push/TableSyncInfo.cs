using System.ComponentModel.DataAnnotations;

namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Describes a table included in a push session with its estimated record count.
/// </summary>
public class TableSyncInfo
{
    /// <summary>Gets or sets the business table name (e.g., <c>Customers</c>).</summary>
    [Required]
    public string TableName { get; set; } = string.Empty;

    /// <summary>Gets or sets the estimated number of records to be pushed for this table.</summary>
    /// <remarks>Used by the server to choose between shared and dedicated temp table strategies.</remarks>
    [Range(0, int.MaxValue)]
    public int EstimatedRecordCount { get; set; }
}
