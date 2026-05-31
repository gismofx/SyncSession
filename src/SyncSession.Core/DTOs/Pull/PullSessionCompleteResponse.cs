namespace SyncSession.Core.DTOs.Pull;

/// <summary>
/// Response from completing a pull session.
/// </summary>
public class PullSessionCompleteResponse
{
    /// <summary>Gets or sets a value indicating whether the pull session completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the error message when the request fails.</summary>
    /// <value>The error description, or <c>null</c> if the request succeeded.</value>
    public string? ErrorMessage { get; set; }
}
