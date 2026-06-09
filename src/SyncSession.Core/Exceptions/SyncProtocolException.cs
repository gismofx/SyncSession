using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Thrown by the client when the server rejects a sync request due to an incompatible
/// protocol version (<c>426 Upgrade Required</c>).
/// </summary>
/// <remarks>
/// Update the <c>SyncSystem.Client</c> (and/or <c>SyncSystem.Server</c>) NuGet package
/// to a version that speaks a compatible protocol to resolve this error.
/// See <see cref="SyncSystem.Core.Constants.SyncProtocolVersion"/> for version details.
/// </remarks>
public class SyncProtocolException : SyncException
{
    /// <summary>The protocol version the client sent.</summary>
    public int ClientVersion { get; }

    /// <summary>The minimum protocol version the server accepts.</summary>
    public int ServerMinVersion { get; }

    /// <summary>The current protocol version the server speaks.</summary>
    public int ServerCurrentVersion { get; }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="clientVersion">Protocol version the client sent.</param>
    /// <param name="serverMinVersion">Minimum protocol version the server accepts.</param>
    /// <param name="serverCurrentVersion">Current protocol version the server speaks.</param>
    public SyncProtocolException(int clientVersion, int serverMinVersion, int serverCurrentVersion)
        : base(BuildMessage(clientVersion, serverMinVersion, serverCurrentVersion))
    {
        ClientVersion = clientVersion;
        ServerMinVersion = serverMinVersion;
        ServerCurrentVersion = serverCurrentVersion;
    }

    /// <inheritdoc cref="SyncException(string)"/>
    public SyncProtocolException(string message) : base(message)
    {
    }

    /// <inheritdoc cref="SyncException(string, Exception)"/>
    public SyncProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private static string BuildMessage(int clientVersion, int serverMinVersion, int serverCurrentVersion)
        => $"SyncSystem protocol version mismatch: client is speaking protocol {clientVersion}, " +
           $"but server requires {serverMinVersion}–{serverCurrentVersion}. " +
           $"Update the SyncSystem.Client NuGet package to resolve this.";
}
