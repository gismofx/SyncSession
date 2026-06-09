using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Exception thrown when a session is not found.
/// </summary>
public class SessionNotFoundException : SyncException
{
    /// <summary>The ID of the session that could not be found.</summary>
    public Guid SessionId { get; }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="sessionId">ID of the missing session.</param>
    public SessionNotFoundException(Guid sessionId) 
        : base($"Session {sessionId} not found")
    {
        SessionId = sessionId;
    }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="sessionId">ID of the missing session.</param>
    /// <param name="message">Custom error message.</param>
    public SessionNotFoundException(Guid sessionId, string message) 
        : base(message)
    {
        SessionId = sessionId;
    }

    /// <inheritdoc cref="SyncException(string, Exception)"/>
    /// <param name="sessionId">ID of the missing session.</param>
    /// <param name="message">Custom error message.</param>
    /// <param name="innerException">Inner exception.</param>
    public SessionNotFoundException(Guid sessionId, string message, Exception innerException) 
        : base(message, innerException)
    {
        SessionId = sessionId;
    }
}
