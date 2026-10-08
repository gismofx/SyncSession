using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using SyncSession.Core.Models;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Strongly-typed handler for table synchronization operations.
/// Eliminates reflection by providing type-safe push/pull methods.
/// </summary>
public interface ITableSyncHandler
{
    /// <summary>
    /// Get the count of dirty (unsynchronized) records for this table
    /// </summary>
    Task<int> GetDirtyCountAsync();
    
    /// <summary>
    /// Push dirty records to the server for this table. Does <b>not</b> mark them clean — the server
    /// has not committed them yet. Pass the result to <see cref="MarkPushedCleanAsync"/> once it has.
    /// </summary>
    /// <param name="sessionId">Push session identifier</param>
    /// <param name="progress">Optional progress reporting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The records pushed, as they were when read; its count is the number pushed.</returns>
    Task<IReadOnlyList<PushedRecordStamp>> PushAsync(
        Guid sessionId,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark records clean after the server has committed the push that carried them. Rows saved
    /// again since the push read them stay dirty.
    /// </summary>
    /// <param name="pushed">The result of <see cref="PushAsync"/> for the committed session.</param>
    Task MarkPushedCleanAsync(IReadOnlyCollection<PushedRecordStamp> pushed);
    
    /// <summary>
    /// Pull records from the server for this table
    /// </summary>
    /// <param name="pullSessionId">Pull session identifier</param>
    /// <param name="transaction">Optional transaction for atomic operations</param>
    /// <param name="progress">Optional progress reporting (Current, Total)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of (records pulled, session IDs processed)</returns>
    Task<(int RecordsPulled, List<Guid> SessionIds)> PullAsync(
        Guid pullSessionId,
        IDbTransaction? transaction = null,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken cancellationToken = default);
}
