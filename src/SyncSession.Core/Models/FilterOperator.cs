namespace SyncSession.Core.Models;

/// <summary>
/// Supported filter operators for <see cref="DataFilter"/> in data query operations.
/// </summary>
public enum FilterOperator
{
    /// <summary>Exact match (column = value).</summary>
    Equals,

    /// <summary>Negated match (column != value).</summary>
    NotEquals,

    /// <summary>Substring match (column LIKE '%value%').</summary>
    Contains,

    /// <summary>Prefix match (column LIKE 'value%').</summary>
    StartsWith,

    /// <summary>Greater than (column &gt; value).</summary>
    GreaterThan,

    /// <summary>Less than (column &lt; value).</summary>
    LessThan,

    /// <summary>Greater than or equal (column &gt;= value).</summary>
    GreaterThanOrEqual,

    /// <summary>Less than or equal (column &lt;= value).</summary>
    LessThanOrEqual,

    /// <summary>Set membership (column IN (value1, value2, ...)).</summary>
    In
}
