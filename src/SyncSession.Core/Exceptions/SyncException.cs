using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Base exception for all SyncSystem-related errors.
/// </summary>
public class SyncException : Exception
{
    /// <inheritdoc/>
    public SyncException()
    {
    }

    /// <inheritdoc/>
    public SyncException(string message) 
        : base(message)
    {
    }

    /// <inheritdoc/>
    public SyncException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}
