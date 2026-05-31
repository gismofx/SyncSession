using System;
using System.Threading;
using System.Threading.Tasks;
using SyncSession.Core.Models;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Interface for client-side synchronization engines.
/// Abstracts sync implementation to support both direct database and HTTP-based sync.
/// </summary>
public interface ISyncEngine : IDisposable
{
    /// <summary>
    /// Perform a full synchronization (push then pull).
    /// </summary>
    /// <param name="progress">Optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">Cancellation token to stop operation.</param>
    /// <returns>Sync result containing record counts and status.</returns>
    Task<SyncResult> SynchronizeAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Push local changes to server.
    /// </summary>
    /// <param name="progress">Optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">Cancellation token to stop operation.</param>
    /// <returns>Number of records pushed.</returns>
    Task<int> PushAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pull server changes to local.
    /// </summary>
    /// <param name="progress">Optional progress reporter for UI updates.</param>
    /// <param name="cancellationToken">Cancellation token to stop operation.</param>
    /// <returns>Number of records pulled.</returns>
    Task<int> PullAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
