namespace SyncSession.Server.Services;

/// <summary>
/// Controls whether the server accepts new sync sessions.
/// When gated, all <c>*/begin</c> entry points return 503 until the gate is cleared.
/// In-flight sessions (batch, complete, status) are unaffected and complete normally.
/// </summary>
public interface ISyncGate
{
    /// <summary>
    /// Returns <c>true</c> when maintenance mode is active and new sessions are blocked.
    /// </summary>
    bool IsGated { get; }

    /// <summary>Enables maintenance mode, blocking new sessions at all entry points.</summary>
    void Enable();

    /// <summary>Disables maintenance mode, restoring normal operation.</summary>
    void Disable();
}
