using System;

namespace SyncSession.Core.Models;

/// <summary>
/// Result of a single-record direct write operation.
/// </summary>
public class DirectWriteResult
{
    /// <summary>
    /// The sync session ID created for this write operation.
    /// All sync clients will see this change when they pull this session.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Number of records written (should always be 1 for single-record operations).
    /// </summary>
    public int RecordsWritten { get; set; }

    /// <summary>
    /// UTC timestamp when the write was committed.
    /// </summary>
    public DateTime CommittedAtUtc { get; set; }
}
