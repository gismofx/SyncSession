using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Exception thrown when a synchronization operation times out.
/// </summary>
public class SyncTimeoutException : SyncException
{
    public TimeSpan Timeout { get; }

    public SyncTimeoutException(TimeSpan timeout) 
        : base($"Synchronization operation timed out after {timeout.TotalSeconds} seconds")
    {
        Timeout = timeout;
    }

    public SyncTimeoutException(TimeSpan timeout, string message) 
        : base(message)
    {
        Timeout = timeout;
    }

    public SyncTimeoutException(TimeSpan timeout, string message, Exception innerException) 
        : base(message, innerException)
    {
        Timeout = timeout;
    }
}
