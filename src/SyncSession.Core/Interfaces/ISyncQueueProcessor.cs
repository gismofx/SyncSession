using System;
using System.Threading;
using System.Threading.Tasks;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Processes committed sync sessions from the background queue.
/// </summary>
public interface ISyncQueueProcessor
{
    /// <summary>
    /// Finds all Ready sessions and processes them sequentially.
    /// Returns the number of sessions processed.
    /// </summary>
    Task<int> ProcessReadySessionsAsync(CancellationToken cancellationToken);
}
