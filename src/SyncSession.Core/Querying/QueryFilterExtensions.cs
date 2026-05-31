using System.Collections.Generic;
using System.Linq;

namespace SyncSession.Core.Querying;

/// <summary>
/// Extension methods for building query filter expressions.
/// Used with <see cref="QueryBuilder{T}"/> to express set membership filters.
/// </summary>
public static class QueryFilterExtensions
{
    /// <summary>
    /// Expresses a set membership filter (SQL <c>IN</c> operator).
    /// Only meaningful inside a <see cref="QueryBuilder{T}.Where"/> expression.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The property value to check.</param>
    /// <param name="collection">The set of allowed values.</param>
    /// <returns>Always throws at runtime — this method is only interpreted by the expression visitor.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown if called directly at runtime instead of inside a query expression.
    /// </exception>
    /// <example>
    /// <code>
    /// query.Where(c => c.Status.In("Active", "Pending"))
    /// </code>
    /// </example>
    public static bool In<T>(this T value, params T[] collection)
    {
        throw new InvalidOperationException(
            "The In() method is only supported inside QueryBuilder<T>.Where() expressions. " +
            "It cannot be called directly at runtime.");
    }

    /// <summary>
    /// Expresses a set membership filter using an <see cref="IEnumerable{T}"/> collection.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The property value to check.</param>
    /// <param name="collection">The set of allowed values.</param>
    /// <returns>Always throws at runtime.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown if called directly at runtime.
    /// </exception>
    public static bool In<T>(this T value, IEnumerable<T> collection)
    {
        throw new InvalidOperationException(
            "The In() method is only supported inside QueryBuilder<T>.Where() expressions. " +
            "It cannot be called directly at runtime.");
    }
}
