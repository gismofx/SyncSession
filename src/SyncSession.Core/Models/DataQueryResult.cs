using System.Collections.Generic;

namespace SyncSession.Core.Models;

/// <summary>
/// Result of a <see cref="DataQuery"/> execution, containing the matched records
/// and pagination metadata.
/// </summary>
public class DataQueryResult
{
    /// <summary>
    /// Table name the query was executed against.
    /// </summary>
    public string Table { get; set; } = string.Empty;

    /// <summary>
    /// Matched records as property-name/value dictionaries.
    /// Column set matches the entity's business + audit properties
    /// (infrastructure properties like IsDirty and SyncSessionId are excluded).
    /// </summary>
    public List<Dictionary<string, object?>> Records { get; set; } = new();

    /// <summary>
    /// Total number of records matching the filter criteria (before pagination).
    /// Use with <see cref="DataQuery.Offset"/> and <see cref="DataQuery.Limit"/> for paging UI.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// The offset applied to this result set.
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// The limit applied to this result set.
    /// </summary>
    public int Limit { get; set; }
}
