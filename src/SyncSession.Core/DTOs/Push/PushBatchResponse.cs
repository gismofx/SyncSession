namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Response from pushing a batch of records.
/// </summary>
public class PushBatchResponse
{
    /// <summary>Gets or sets a value indicating whether the batch was accepted successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the number of records accepted into the temp table.</summary>
    public int RecordsAccepted { get; set; }

    /// <summary>Gets or sets the error message when the request fails.</summary>
    /// <value>The error description, or <c>null</c> if the request succeeded.</value>
    public string? ErrorMessage { get; set; }
}
