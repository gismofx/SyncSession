using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SyncSession.Core.DTOs;
using SyncSession.Core.Models;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Server contract for streaming a full-tenant seed payload to a client.
/// Implemented by <c>HttpSeedServerApi</c> in SyncSystem.Client for HTTP communication,
/// and by <c>SeedService</c> on the server side for direct calls.
/// </summary>
/// <remarks>
/// The stream is NDJSON (newline-delimited JSON). Each line is a <see cref="SeedLine"/>
/// discriminated by <c>Type</c>: begin → table → row(s) → table_end → … → end.
/// The <c>anchor</c> value on the <c>end</c> line is the UTC timestamp captured before
/// the first query. Pass it as the <c>since</c> parameter to the first incremental pull
/// to guarantee no records are missed.
/// </remarks>
public interface ISeedServerApi
{
    /// <summary>
    /// Opens a streaming seed connection for the specified tenant and yields
    /// <see cref="SeedLine"/> values as they are produced by the server.
    /// </summary>
    /// <param name="tenantId">Tenant whose records should be seeded.</param>
    /// <param name="ct">Cancellation token. Cancellation aborts the stream cleanly.</param>
    /// <returns>
    /// An async sequence of <see cref="SeedLine"/> values in begin/table/row/table_end/end order.
    /// </returns>
    IAsyncEnumerable<SeedLine> StreamSeedAsync(Guid tenantId, Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// Posts a seed outcome (Complete, Cancelled, or Failed) to the server.
    /// Marks sessions as processed for Complete status and writes a SyncActivityLog entry.
    /// </summary>
    Task AcknowledgeSeedAsync(SeedAcknowledgeRequest request, CancellationToken ct = default);
}
