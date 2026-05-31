namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Response from completing a push session.
/// </summary>
public class PushSessionCompleteResponse
{
    /// <summary>Gets or sets a value indicating whether the session was successfully queued for processing.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a value indicating whether the session was queued for background processing.</summary>
    public bool QueuedForProcessing { get; set; }

    /// <summary>Gets or sets the error message when the request fails.</summary>
    /// <value>The error description, or <c>null</c> if the request succeeded.</value>
    public string? ErrorMessage { get; set; }
}
