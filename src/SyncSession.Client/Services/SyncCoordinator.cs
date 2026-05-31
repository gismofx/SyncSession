using System;
using System.Threading;
using System.Threading.Tasks;
using SyncSession.Client.Engine;
using SyncSession.Client.Utilities;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Client.Services;

/// <summary>
/// High-level coordinator for sync operations.
/// Simplifies common sync scenarios for application developers.
/// </summary>
public class SyncCoordinator
{
    private readonly ClientSyncEngine _syncEngine;
    private readonly NetworkHelper _networkHelper;
    private readonly RetryPolicy _retryPolicy;

    /// <summary>
    /// Initializes a new instance of <see cref="SyncCoordinator"/>.
    /// </summary>
    /// <param name="syncEngine">The underlying sync engine to delegate operations to.</param>
    /// <param name="networkHelper">Optional network availability checker; uses default if <c>null</c>.</param>
    /// <param name="retryPolicy">Optional retry policy for transient failures; uses default if <c>null</c>.</param>
    public SyncCoordinator(
        ClientSyncEngine syncEngine,
        NetworkHelper? networkHelper = null,
        RetryPolicy? retryPolicy = null)
    {
        _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
        _networkHelper = networkHelper ?? new NetworkHelper();
        _retryPolicy = retryPolicy ?? new RetryPolicy();
    }

    /// <summary>
    /// Perform a full sync with automatic retry and network checking.
    /// </summary>
    /// <param name="progress">Optional progress reporter for UI updates.</param>
    /// <param name="requireNetwork">Whether to check network availability before syncing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SyncResult> SyncAsync(
        IProgress<SyncProgress>? progress = null,
        bool requireNetwork = true,
        CancellationToken cancellationToken = default)
    {
        if (requireNetwork && !_networkHelper.IsNetworkAvailable())
        {
            return new SyncResult
            {
                Success = false,
                ErrorMessage = "Network unavailable",
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow
            };
        }

        return await _retryPolicy.ExecuteAsync(
            () => _syncEngine.SynchronizeAsync(progress, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Push only (upload local changes).
    /// </summary>
    /// <param name="progress">Optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> PushAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_networkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network unavailable");

        return await _retryPolicy.ExecuteAsync(
            () => _syncEngine.PushAsync(progress, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Pull only (download server changes).
    /// </summary>
    /// <param name="progress">Optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> PullAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_networkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network unavailable");

        return await _retryPolicy.ExecuteAsync(
            () => _syncEngine.PullAsync(progress, cancellationToken),
            cancellationToken);
    }
}
