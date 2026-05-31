using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;

namespace SyncSession.Core.Querying;

/// <summary>
/// Fluent builder that translates LINQ-style expressions into a <see cref="DataQuery"/>
/// suitable for the <c>POST /api/v1/data/{table}/query</c> endpoint.
/// </summary>
/// <typeparam name="T">
/// Entity type implementing <see cref="ISyncEntity"/>. Used for compile-time
/// property validation and column name extraction.
/// </typeparam>
/// <example>
/// <code>
/// var query = new QueryBuilder&lt;Customer&gt;()
///     .Where(c =&gt; c.Name.Contains("Corp"))
///     .Where(c =&gt; c.ModifiedAtUtc &gt; cutoffDate)
///     .OrderByDescending(c =&gt; c.ModifiedAtUtc)
///     .Skip(0).Take(25)
///     .ToDataQuery();
/// </code>
/// </example>
public class QueryBuilder<T> where T : ISyncEntity
{
    private readonly List<DataFilter> _filters = new();
    private readonly HashSet<string> _validColumns;
    private int _offset;
    private int _limit = 50;
    private string? _orderBy;
    private bool _orderDescending = true;
    private bool _includeDeleted;

    /// <summary>
    /// Creates a new query builder. Discovers valid column names from <typeparamref name="T"/>
    /// via <see cref="EntityReflectionHelper"/>.
    /// </summary>
    public QueryBuilder()
    {
        _validColumns = EntityReflectionHelper.GetAllPropertyNames<T>().ToHashSet();
    }

    /// <summary>
    /// Creates a new query builder with an explicit set of valid column names.
    /// Useful for testing or when EntityReflectionHelper is not initialized.
    /// </summary>
    internal QueryBuilder(HashSet<string> validColumns)
    {
        _validColumns = validColumns ?? throw new ArgumentNullException(nameof(validColumns));
    }

    /// <summary>
    /// Adds a filter condition. Multiple <c>Where</c> calls are combined with AND logic.
    /// </summary>
    /// <param name="predicate">
    /// A lambda expression representing the filter (e.g., <c>c =&gt; c.Name == "Acme"</c>).
    /// Supported: comparisons (==, !=, &gt;, &lt;, &gt;=, &lt;=),
    /// string methods (.Contains(), .StartsWith()), and .In().
    /// </param>
    /// <returns>This builder for fluent chaining.</returns>
    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        if (predicate == null)
            throw new ArgumentNullException(nameof(predicate));

        var visitor = new FilterExpressionVisitor(_validColumns);
        var filters = visitor.ExtractFilters(predicate.Body);
        _filters.AddRange(filters);
        return this;
    }

    /// <summary>
    /// Sets ascending ordering by the specified property.
    /// </summary>
    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _orderBy = ExtractPropertyName(keySelector);
        _orderDescending = false;
        return this;
    }

    /// <summary>
    /// Sets descending ordering by the specified property.
    /// </summary>
    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _orderBy = ExtractPropertyName(keySelector);
        _orderDescending = true;
        return this;
    }

    /// <summary>
    /// Sets the number of records to skip (zero-based offset).
    /// </summary>
    public QueryBuilder<T> Skip(int offset)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be >= 0.");
        _offset = offset;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of records to return.
    /// </summary>
    public QueryBuilder<T> Take(int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be > 0.");
        _limit = limit;
        return this;
    }

    /// <summary>
    /// Includes soft-deleted records in the results.
    /// By default, records with <c>IsDeleted = true</c> are excluded.
    /// </summary>
    public QueryBuilder<T> IncludeDeleted()
    {
        _includeDeleted = true;
        return this;
    }

    /// <summary>
    /// Builds the <see cref="DataQuery"/> from the current builder state.
    /// </summary>
    public DataQuery ToDataQuery()
    {
        return new DataQuery
        {
            Filters = _filters.Count > 0 ? new List<DataFilter>(_filters) : null,
            Offset = _offset,
            Limit = _limit,
            OrderBy = _orderBy,
            OrderDescending = _orderDescending,
            IncludeDeleted = _includeDeleted
        };
    }

    private string ExtractPropertyName<TKey>(Expression<Func<T, TKey>> selector)
    {
        var body = selector.Body;

        // Unwrap Convert (e.g., nullable property access)
        if (body is UnaryExpression unary
            && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            body = unary.Operand;

        if (body is MemberExpression member)
        {
            var name = member.Member.Name;
            if (!_validColumns.Contains(name))
                throw new ArgumentException(
                    $"Unknown column '{name}'. Valid columns: {string.Join(", ", _validColumns.OrderBy(c => c))}.");
            return name;
        }

        throw new NotSupportedException(
            $"Expected a property access expression, got: {selector.Body}.");
    }
}
