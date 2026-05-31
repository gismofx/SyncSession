using System.Collections.Generic;

namespace SyncSession.Core.Models;

/// <summary>
/// Describes a filtered, paginated query against a sync-enabled table.
/// Sent as the body of <c>POST /api/v1/data/{table}/query</c>.
/// Can be built manually or via <see cref="SyncSystem.Core.Querying.QueryBuilder{T}"/>.
/// </summary>
public class DataQuery
{
    /// <summary>
    /// Zero or more filter conditions. All filters are combined with AND logic.
    /// </summary>
    public List<DataFilter>? Filters { get; set; }

    /// <summary>
    /// Number of records to skip (zero-based). Default: 0.
    /// </summary>
    public int Offset { get; set; } = 0;

    /// <summary>
    /// Maximum number of records to return. Default: 50. Server may enforce a hard cap.
    /// </summary>
    public int Limit { get; set; } = 50;

    /// <summary>
    /// Column name to order results by. Default: null (server chooses, typically ModifiedAtUtc).
    /// </summary>
    public string? OrderBy { get; set; }

    /// <summary>
    /// When <c>true</c>, results are sorted descending. Default: true.
    /// </summary>
    public bool OrderDescending { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, includes soft-deleted records (<c>IsDeleted = true</c>).
    /// Default: false (soft-deleted records are excluded).
    /// </summary>
    public bool IncludeDeleted { get; set; } = false;
}
