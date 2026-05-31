namespace SyncSession.Core.Models;

/// <summary>
/// A single filter condition for a <see cref="DataQuery"/>.
/// Column names are validated against registered entity properties at execution time.
/// All values are parameterized — never interpolated into SQL.
/// </summary>
public class DataFilter
{
    /// <summary>
    /// Entity property/column name to filter on (e.g., "Name", "ModifiedAtUtc").
    /// Validated against registered entity columns; unknown columns produce a 400 error.
    /// </summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>
    /// Comparison operator to apply.
    /// </summary>
    public FilterOperator Operator { get; set; }

    /// <summary>
    /// Filter value. Type should match the target column
    /// (string for text, DateTime for timestamps, etc.).
    /// For <see cref="FilterOperator.In"/>, provide an array or list.
    /// </summary>
    public object? Value { get; set; }
}
