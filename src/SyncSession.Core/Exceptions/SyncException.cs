using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Base exception for all SyncSystem-related errors.
/// </summary>
public class SyncException : Exception
{
    public SyncException()
    {
    }

    public SyncException(string message) 
        : base(message)
    {
    }

    public SyncException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}
