using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Exception thrown when a session is not found.
/// </summary>
public class SessionNotFoundException : SyncException
{
    public Guid SessionId { get; }

    public SessionNotFoundException(Guid sessionId) 
        : base($"Session {sessionId} not found")
    {
        SessionId = sessionId;
    }

    public SessionNotFoundException(Guid sessionId, string message) 
        : base(message)
    {
        SessionId = sessionId;
    }

    public SessionNotFoundException(Guid sessionId, string message, Exception innerException) 
        : base(message, innerException)
    {
        SessionId = sessionId;
    }
}
