using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using SyncSession.Core.Models;

namespace SyncSession.Core.Querying;

/// <summary>
/// Walks a <see cref="Expression{TDelegate}"/> tree and extracts
/// <see cref="DataFilter"/> instances for supported comparison and method-call patterns.
/// </summary>
internal sealed class FilterExpressionVisitor : ExpressionVisitor
{
    private readonly List<DataFilter> _filters = new();
    private readonly HashSet<string> _validColumns;

    /// <summary>
    /// Initializes a new visitor that validates column names against <paramref name="validColumns"/>.
    /// </summary>
    public FilterExpressionVisitor(HashSet<string> validColumns)
    {
        _validColumns = validColumns ?? throw new ArgumentNullException(nameof(validColumns));
    }

    /// <summary>
    /// Extracts filters from the given expression.
    /// </summary>
    public List<DataFilter> ExtractFilters(Expression expression)
    {
        _filters.Clear();
        Visit(expression);
        return new List<DataFilter>(_filters);
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        // Handle AND (&&) — recurse into both sides
        if (node.NodeType == ExpressionType.AndAlso)
        {
            Visit(node.Left);
            Visit(node.Right);
            return node;
        }

        // Try to extract column + value from comparison
        if (TryExtractMemberAndValue(node.Left, node.Right, out var columnName, out var value))
        {
            var op = MapBinaryOperator(node.NodeType, isReversed: false);
            _filters.Add(new DataFilter { Column = columnName, Operator = op, Value = value });
            return node;
        }

        // Try reversed (e.g., 5 > x  →  x < 5)
        if (TryExtractMemberAndValue(node.Right, node.Left, out columnName, out value))
        {
            var op = MapBinaryOperator(node.NodeType, isReversed: true);
            _filters.Add(new DataFilter { Column = columnName, Operator = op, Value = value });
            return node;
        }

        throw new NotSupportedException(
            $"Unsupported binary expression: {node}. " +
            "Ensure one side is a property access and the other is a constant or captured variable.");
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // string.Contains(value)
        if (node.Method.Name == "Contains" && node.Object != null
            && node.Method.DeclaringType == typeof(string))
        {
            var columnName = ExtractMemberName(node.Object);
            var value = EvaluateExpression(node.Arguments[0]);
            _filters.Add(new DataFilter { Column = columnName, Operator = FilterOperator.Contains, Value = value });
            return node;
        }

        // string.StartsWith(value)
        if (node.Method.Name == "StartsWith" && node.Object != null
            && node.Method.DeclaringType == typeof(string))
        {
            var columnName = ExtractMemberName(node.Object);
            var value = EvaluateExpression(node.Arguments[0]);
            _filters.Add(new DataFilter { Column = columnName, Operator = FilterOperator.StartsWith, Value = value });
            return node;
        }

        // .In() extension method
        if (node.Method.Name == "In" && node.Method.DeclaringType == typeof(QueryFilterExtensions))
        {
            var columnName = ExtractMemberName(node.Arguments[0]);
            var collection = EvaluateExpression(node.Arguments[1]);

            // Flatten to object list
            var values = FlattenCollection(collection);
            _filters.Add(new DataFilter { Column = columnName, Operator = FilterOperator.In, Value = values });
            return node;
        }

        throw new NotSupportedException(
            $"Unsupported method call: {node.Method.DeclaringType?.Name}.{node.Method.Name}(). " +
            "Supported methods: string.Contains(), string.StartsWith(), .In().");
    }

    protected override Expression VisitUnary(UnaryExpression node)
    {
        // Handle implicit conversions (e.g., DateTime? comparison boxing)
        if (node.NodeType == ExpressionType.Convert || node.NodeType == ExpressionType.ConvertChecked)
            return Visit(node.Operand);

        return base.VisitUnary(node);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private bool TryExtractMemberAndValue(
        Expression candidateMember, Expression candidateValue,
        out string columnName, out object? value)
    {
        columnName = null!;
        value = null;

        // Unwrap Convert nodes (e.g., nullable comparisons)
        var unwrappedMember = UnwrapConvert(candidateMember);

        if (unwrappedMember is MemberExpression memberExpr
            && IsEntityProperty(memberExpr))
        {
            columnName = memberExpr.Member.Name;
            ValidateColumn(columnName);
            value = EvaluateExpression(candidateValue);
            return true;
        }

        return false;
    }

    private string ExtractMemberName(Expression expression)
    {
        var unwrapped = UnwrapConvert(expression);

        if (unwrapped is MemberExpression member && IsEntityProperty(member))
        {
            ValidateColumn(member.Member.Name);
            return member.Member.Name;
        }

        throw new NotSupportedException(
            $"Expected a property access expression, got: {expression}.");
    }

    private static Expression UnwrapConvert(Expression expression)
    {
        while (expression is UnaryExpression unary
               && (unary.NodeType == ExpressionType.Convert
                   || unary.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unary.Operand;
        }
        return expression;
    }

    private static bool IsEntityProperty(MemberExpression member)
    {
        // Direct property on the lambda parameter (e.g., c.Name)
        return member.Expression is ParameterExpression
               || (member.Expression is UnaryExpression u && u.Operand is ParameterExpression);
    }

    private void ValidateColumn(string columnName)
    {
        if (!_validColumns.Contains(columnName))
            throw new ArgumentException(
                $"Unknown column '{columnName}'. Valid columns: {string.Join(", ", _validColumns.OrderBy(c => c))}.");
    }

    private static FilterOperator MapBinaryOperator(ExpressionType nodeType, bool isReversed)
    {
        return nodeType switch
        {
            ExpressionType.Equal => FilterOperator.Equals,
            ExpressionType.NotEqual => FilterOperator.NotEquals,
            ExpressionType.GreaterThan => isReversed ? FilterOperator.LessThan : FilterOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => isReversed ? FilterOperator.LessThanOrEqual : FilterOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => isReversed ? FilterOperator.GreaterThan : FilterOperator.LessThan,
            ExpressionType.LessThanOrEqual => isReversed ? FilterOperator.GreaterThanOrEqual : FilterOperator.LessThanOrEqual,
            _ => throw new NotSupportedException(
                $"Unsupported comparison operator: {nodeType}. " +
                "Supported: ==, !=, >, <, >=, <=.")
        };
    }

    private static object? EvaluateExpression(Expression expression)
    {
        // Constant (e.g., "Corp", 42, null)
        if (expression is ConstantExpression constant)
            return constant.Value;

        // Compile and invoke for captured variables, method calls, etc.
        try
        {
            var lambda = Expression.Lambda(expression);
            var compiled = lambda.Compile();
            return compiled.DynamicInvoke();
        }
        catch (Exception ex)
        {
            throw new NotSupportedException(
                $"Unable to evaluate expression: {expression}. " +
                "Filter values must be constants or captured variables.", ex);
        }
    }

    private static List<object?> FlattenCollection(object? collection)
    {
        if (collection is IEnumerable enumerable and not string)
            return enumerable.Cast<object?>().ToList();

        throw new ArgumentException(
            "The In() operator requires an array or IEnumerable collection as its argument.");
    }
}
