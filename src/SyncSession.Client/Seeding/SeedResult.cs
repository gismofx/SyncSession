using System;
using System.Collections.Generic;

namespace SyncSession.Client.Seeding;

/// <summary>
/// Result returned by <see cref="SeedClient.SeedAsync"/> after the stream completes.
/// </summary>
/// <param name="Anchor">
/// UTC timestamp captured by the server before the first query.
/// Pass as the <c>since</c> parameter to the first <c>BeginPullAsync</c> call
/// to guarantee no records written during streaming are missed.
/// </param>
/// <param name="RowCountsByTable">Total rows written per table name.</param>
public sealed record SeedResult(
    DateTime Anchor,
    IReadOnlyDictionary<string, int> RowCountsByTable);
