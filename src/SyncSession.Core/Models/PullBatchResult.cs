namespace SyncSession.Core.Models;

/// <summary>
/// Result of a pull batch query — records plus pagination metadata.
/// </summary>
/// <remarks>
/// Returned by GetPullBatchAsync to provide records and pagination info in a single response.
/// HasMore indicates whether additional records exist beyond this batch.
/// </remarks>
public readonly struct PullBatchResult
{
    /// <summary>
    /// Records in this batch as dictionaries (flexible type support).
    /// </summary>
    public List<Dictionary<string, object?>> Records { get; init; }
    
    /// <summary>
    /// True if more records exist beyond this batch (pagination).
    /// </summary>
    public bool HasMore { get; init; }
    
    /// <summary>
    /// Total number of records available (not just in this batch).
    /// </summary>
    public int TotalRecords { get; init; }
}
