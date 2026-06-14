using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Thrown by <c>SyncCoordinator</c> when a network-gated operation is attempted while no
/// network is available and <c>requireNetwork</c> is set. This is an expected, recoverable
/// runtime condition (not a programming error), so callers can catch it distinctly and prompt
/// a retry when connectivity returns.
/// </summary>
public class NetworkUnavailableException : SyncException
{
    /// <inheritdoc cref="SyncException()"/>
    public NetworkUnavailableException()
        : base("Network unavailable.")
    {
    }

    /// <inheritdoc cref="SyncException(string)"/>
    public NetworkUnavailableException(string message)
        : base(message)
    {
    }

    /// <inheritdoc cref="SyncException(string, Exception)"/>
    public NetworkUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
