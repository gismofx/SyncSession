using System;
using System.Collections.Generic;
using System.Linq;

namespace SyncSession.Core.Models;

/// <summary>
/// Result of a batch direct write operation across multiple tables.
/// All records in the batch share the same SyncSessionId and are committed atomically.
/// </summary>
public class DirectWriteBatchResult
{
    /// <summary>
    /// The sync session ID created for this batch operation.
    /// All sync clients will see all changes when they pull this session.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Results per table, keyed by table name.
    /// </summary>
    public Dictionary<string, TableWriteResult> Tables { get; set; } = new();

    /// <summary>
    /// UTC timestamp when the batch was committed.
    /// </summary>
    public DateTime CommittedAtUtc { get; set; }

    /// <summary>
    /// Total records written across all tables.
    /// </summary>
    public int TotalRecordsWritten => Tables.Values.Sum(t => t.RecordsWritten);
}

/// <summary>
/// Write result for a specific table within a batch operation.
/// </summary>
public class TableWriteResult
{
    /// <summary>
    /// Number of records written to this table.
    /// </summary>
    public int RecordsWritten { get; set; }
}
