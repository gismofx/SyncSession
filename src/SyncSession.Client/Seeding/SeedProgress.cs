using System;

namespace SyncSession.Client.Seeding;

/// <summary>Progress snapshot emitted by <see cref="SeedClient.SeedAsync"/> via <c>IProgress&lt;SeedProgress&gt;</c>.</summary>
/// <param name="CurrentTable">Table currently being streamed.</param>
/// <param name="RowsWritten">Rows written for <paramref name="CurrentTable"/> so far.</param>
/// <param name="TotalRows">Advisory total for <paramref name="CurrentTable"/>; -1 if unavailable.</param>
/// <param name="TablesComplete">Number of tables fully written.</param>
/// <param name="TotalTables">Total number of tables in the stream.</param>
/// <param name="Message">Optional status message for display in progress UI.</param>
public sealed record SeedProgress(
    string CurrentTable,
    int RowsWritten,
    int TotalRows,
    int TablesComplete,
    int TotalTables,
    string? Message = null);
