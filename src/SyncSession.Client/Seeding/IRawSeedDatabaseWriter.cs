using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SyncSession.Client.Seeding;

/// <summary>
/// Optional high-performance extension of <see cref="ISeedDatabaseWriter"/>.
/// When implemented, <see cref="SeedClient"/> skips <c>Dictionary&lt;string, object?&gt;</c>
/// allocation and re-serialization by passing raw NDJSON line strings directly.
/// </summary>
/// <remarks>
/// Consumers implement this alongside <see cref="ISeedDatabaseWriter"/> to opt in to
/// the raw path. <see cref="ISeedDatabaseWriter.WriteRowsAsync"/> is never called when
/// the raw path is active — <see cref="WriteRowsRawAsync"/> is called instead.
///
/// Each string in <paramref name="rawLines"/> is a complete NDJSON line as received
/// from the server, e.g.:
/// <code>{"type":"row","table":"History","data":{"Id":"...","PatientId":"...",...}}</code>
/// Implementations use <c>JsonDocument</c> + <c>GetRawText()</c> to extract the
/// <c>data</c> portion without re-serialization.
/// </remarks>
public interface IRawSeedDatabaseWriter
{
    /// <summary>
    /// Called with successive batches of raw NDJSON line strings for a table.
    /// Lines may be single <c>row</c> lines or multi-row <c>rows</c> bundle lines.
    /// Returns the number of rows actually written, for progress tracking.
    /// </summary>
    /// <param name="tableName">Name of the table being written.</param>
    /// <param name="rawLines">Batch of raw NDJSON lines (row or rows bundles).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of rows written to the database.</returns>
    Task<int> WriteRowsRawAsync(string tableName, IReadOnlyList<string> rawLines, CancellationToken ct = default);
}
