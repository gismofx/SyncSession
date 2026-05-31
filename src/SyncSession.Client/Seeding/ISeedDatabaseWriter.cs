using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SyncSession.Client.Seeding;

/// <summary>
/// Consumer-implemented interface for writing seed data into the client database.
/// SyncSession owns stream parsing and batching; consumers own the write mechanism
/// (wa-sqlite, SQLite-net, Microsoft.Data.Sqlite, etc.).
/// </summary>
/// <remarks>
/// <see cref="ClientDatabaseSeedWriter"/> provides a built-in implementation for
/// consumers using <see cref="SyncSystem.Core.Interfaces.IClientDatabase"/>.
/// </remarks>
public interface ISeedDatabaseWriter
{
    /// <summary>Called once before rows are written for a table. Use to open a transaction or perform setup.</summary>
    /// <param name="tableName">Name of the table about to be written.</param>
    /// <param name="totalRows">Advisory row count; -1 if unavailable.</param>
    /// <param name="ct">Cancellation token.</param>
    Task BeginTableAsync(string tableName, int totalRows, CancellationToken ct = default);

    /// <summary>
    /// Called with successive batches of rows for a table.
    /// May be called zero or more times per table.
    /// </summary>
    /// <param name="tableName">Name of the table being written.</param>
    /// <param name="rows">Batch of records as column → value pairs.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteRowsAsync(string tableName, IReadOnlyList<Dictionary<string, object?>> rows, CancellationToken ct = default);

    /// <summary>Called once after all rows for a table have been written. Use to commit a per-table transaction.</summary>
    /// <param name="tableName">Name of the completed table.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EndTableAsync(string tableName, CancellationToken ct = default);

    /// <summary>Called once after all tables are written. Use to commit an outer transaction or finalize the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task CommitAsync(CancellationToken ct = default);
}
