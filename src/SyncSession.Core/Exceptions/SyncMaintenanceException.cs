using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Thrown by the client when the server refuses to begin a new sync because it is gated —
/// maintenance mode is enabled, or nothing is serving in front of it (<c>503</c>).
/// </summary>
/// <remarks>
/// Only begin-class calls are gated; a session already in flight runs to completion. The
/// condition is transient, so a caller may retry after <see cref="RetryAfterSeconds"/>.
/// <para>
/// The <see cref="Exception.Message"/> is diagnostic, for logs — it is deliberately not written
/// for end users. An application shows its own wording, using <see cref="RetryAfterSeconds"/>
/// and <see cref="ServerMessage"/> as the facts to build it from.
/// </para>
/// </remarks>
public class SyncMaintenanceException : SyncException
{
    /// <summary>Retry delay assumed when the response carries no usable <c>Retry-After</c>.</summary>
    public const int DefaultRetryAfterSeconds = 60;

    /// <summary>Seconds the server asked the caller to wait before retrying.</summary>
    public int RetryAfterSeconds { get; }

    /// <summary>
    /// The reason the server gave, when the 503 carried one. Null when the response body was
    /// empty or unreadable — which is what a 503 from something in front of the app looks like.
    /// </summary>
    public string? ServerMessage { get; }

    /// <summary>Creates the exception from what the 503 response carried.</summary>
    /// <param name="serverMessage">The server's stated reason, or null if it gave none.</param>
    /// <param name="retryAfterSeconds">Seconds to wait before retrying.</param>
    public SyncMaintenanceException(string? serverMessage, int retryAfterSeconds)
        : base(BuildMessage(serverMessage, retryAfterSeconds))
    {
        ServerMessage = serverMessage;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <inheritdoc cref="SyncException(string)"/>
    public SyncMaintenanceException(string message)
        : base(message) => RetryAfterSeconds = DefaultRetryAfterSeconds;

    /// <inheritdoc cref="SyncException(string, Exception)"/>
    public SyncMaintenanceException(string message, Exception innerException)
        : base(message, innerException) => RetryAfterSeconds = DefaultRetryAfterSeconds;

    private static string BuildMessage(string? serverMessage, int retryAfterSeconds)
        => string.IsNullOrWhiteSpace(serverMessage)
            ? $"The sync server returned 503 with no stated reason; retry after {retryAfterSeconds}s."
            : $"The sync server refused a new sync: {serverMessage} (retry after {retryAfterSeconds}s)";
}
