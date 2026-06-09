using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Exception thrown when a synchronization operation times out.
/// </summary>
public class SyncTimeoutException : SyncException
{
    /// <summary>The duration after which the operation timed out.</summary>
    public TimeSpan Timeout { get; }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="timeout">Duration after which the operation timed out.</param>
    public SyncTimeoutException(TimeSpan timeout) 
        : base($"Synchronization operation timed out after {timeout.TotalSeconds} seconds")
    {
        Timeout = timeout;
    }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="timeout">Duration after which the operation timed out.</param>
    /// <param name="message">Custom error message.</param>
    public SyncTimeoutException(TimeSpan timeout, string message) 
        : base(message)
    {
        Timeout = timeout;
    }

    /// <inheritdoc cref="SyncException(string, Exception)"/>
    /// <param name="timeout">Duration after which the operation timed out.</param>
    /// <param name="message">Custom error message.</param>
    /// <param name="innerException">Inner exception.</param>
    public SyncTimeoutException(TimeSpan timeout, string message, Exception innerException) 
        : base(message, innerException)
    {
        Timeout = timeout;
    }
}
