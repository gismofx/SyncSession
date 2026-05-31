namespace SyncSession.Core.Constants;

/// <summary>
/// Defines the SyncSystem wire protocol version negotiated between client and server.
/// </summary>
/// <remarks>
/// <para>
/// Protocol version is an integer that is <b>independent of the NuGet package version</b>.
/// It increments only on breaking changes to the sync HTTP API — removed endpoints,
/// incompatible request/response shapes, or changed required fields.
/// A package version bump that adds optional features or fixes bugs does NOT change
/// the protocol version.
/// </para>
/// <para>
/// <b>Single source of truth:</b> both <c>SyncSystem.Client</c> and <c>SyncSystem.Server</c>
/// reference these constants from <c>SyncSystem.Core</c>. When a breaking protocol change
/// ships, update <see cref="Current"/> (and optionally <see cref="MinSupported"/>) here only.
/// </para>
/// <para>
/// <b>Compatibility window:</b> the server accepts clients where
/// <c>MinSupported &lt;= clientVersion &lt;= Current</c>. When protocol 2 ships,
/// keep <c>MinSupported = 1</c> temporarily to allow old clients to keep working,
/// then bump <c>MinSupported = 2</c> in a later release to force upgrades.
/// </para>
/// </remarks>
public static class SyncProtocolVersion
{
    /// <summary>
    /// The protocol version this build of SyncSystem speaks.
    /// Sent by the client on every <c>BeginPush</c> and <c>BeginPull</c> request.
    /// </summary>
    public const int Current = 1;

    /// <summary>
    /// The oldest protocol version the server will accept.
    /// Clients below this version receive <c>426 Upgrade Required</c>.
    /// </summary>
    public const int MinSupported = 1;

    /// <summary>
    /// HTTP header name used to carry the client's protocol version.
    /// Value is <see cref="Current"/> as a string (e.g. <c>"1"</c>).
    /// </summary>
    public const string ProtocolHeader = "X-SyncSystem-Protocol";

    /// <summary>
    /// HTTP header name used to carry the client's NuGet package version.
    /// Informational only — the server logs it but never rejects based on it.
    /// </summary>
    public const string PackageVersionHeader = "X-SyncSystem-Version";
}
