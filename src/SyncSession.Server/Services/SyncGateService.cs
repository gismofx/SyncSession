namespace SyncSession.Server.Services;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="ISyncGate"/>.
/// Registered as a singleton — resets to disabled on server restart (no persistence required).
/// </summary>
public sealed class SyncGateService : ISyncGate
{
    private volatile bool _isGated;

    /// <inheritdoc />
    public bool IsGated => _isGated;

    /// <inheritdoc />
    public void Enable() => _isGated = true;

    /// <inheritdoc />
    public void Disable() => _isGated = false;
}
