using System.Collections.Generic;

namespace SyncSession.Core.Models;

/// <summary>
/// What happened for one table when the library ensured the indexes its own queries need.
/// </summary>
/// <remarks>
/// Reported per table so a single unusable table cannot stop the rest from being fixed, and so an
/// operator can see from the log which tables were touched and which were left alone.
/// </remarks>
public enum SyncIndexOutcome
{
    /// <summary>The index was created.</summary>
    Created,

    /// <summary>
    /// An existing index already satisfies the requirement, whatever it is named. Nothing was issued.
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// The index is missing and would have been created, but <c>createMissing</c> was false.
    /// This is what <c>LogOnly</c> reports.
    /// </summary>
    WouldCreate,

    /// <summary>
    /// The table does not exist in this database, so nothing was attempted. A registered entity table
    /// can legitimately be absent on a partially-provisioned deployment.
    /// </summary>
    Skipped,

    /// <summary>
    /// Creation was attempted and the database refused it. <see cref="SyncIndexAction.Detail"/> carries
    /// the reason; the remaining tables are still processed.
    /// </summary>
    Failed
}

/// <summary>
/// The result of ensuring one sync index on one table.
/// </summary>
/// <param name="TableName">The registered entity table.</param>
/// <param name="IndexName">
/// The index this run would create. When <see cref="Outcome"/> is
/// <see cref="SyncIndexOutcome.AlreadyPresent"/> the satisfying index may carry a different name —
/// existence is decided by column order, never by name.
/// </param>
/// <param name="Columns">The required columns, in index order.</param>
/// <param name="Outcome">What was done.</param>
/// <param name="Detail">
/// Human-readable context for the operator: the failure reason, or the name of the pre-existing index
/// that satisfied the requirement.
/// </param>
public sealed record SyncIndexAction(
    string TableName,
    string IndexName,
    IReadOnlyList<string> Columns,
    SyncIndexOutcome Outcome,
    string? Detail = null);
