using System;
using System.Collections.Generic;
using System.Threading;
using SyncSession.Core.Models;

namespace SyncSession.Server.Services;

/// <summary>
/// Produces a streaming NDJSON seed payload for a tenant.
/// Consumed by <c>SyncController.StreamSeed</c> and exposed to clients via
/// <see cref="SyncSession.Core.Interfaces.ISeedServerApi"/>.
/// </summary>
public interface ISeedService
{
    /// <summary>
    /// Streams all records for <paramref name="tenantId"/> across every registered table,
    /// ordered by <c>[SyncTable(Priority)]</c>, as <see cref="SeedLine"/> values.
    /// </summary>
    /// <param name="tenantId">Tenant whose records to stream.</param>
    /// <param name="deviceId">
    /// Device performing the seed. One snapshot is kept per device and tenant, so a resumed seed
    /// reads the same snapshot.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Async sequence in begin → (table → rows → table_end)* → end order.
    /// The <c>end</c> line carries the anchor timestamp for the first incremental pull.
    /// </returns>
    IAsyncEnumerable<SeedLine> StreamSeedAsync(Guid tenantId, Guid deviceId, CancellationToken ct = default);
}
