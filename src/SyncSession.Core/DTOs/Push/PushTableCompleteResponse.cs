namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Response from marking a table as complete in a push session.
/// </summary>
public class PushTableCompleteResponse
{
    /// <summary>Gets or sets a value indicating whether the table was completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the actual number of records the server received for this table.</summary>
    public int ActualRecordCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the actual record count matches the client's sent count.</summary>
    public bool CountMatches { get; set; }

    /// <summary>Gets or sets the error message when the request fails.</summary>
    /// <value>The error description, or <c>null</c> if the request succeeded.</value>
    public string? ErrorMessage { get; set; }
}
