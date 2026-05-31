using System;
using SyncSession.Core.Validation;

namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Request to complete a push session and queue it for background processing.
/// </summary>
public class PushSessionCompleteRequest
{
    /// <summary>Gets or sets the session ID to mark as ready for processing.</summary>
    [RequiredGuid]
    public Guid SessionId { get; set; }
}
