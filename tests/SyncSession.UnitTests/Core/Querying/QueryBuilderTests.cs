using System;
using System.Collections.Generic;
using FluentAssertions;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Querying;
using Xunit;

namespace SyncSession.UnitTests.Core.Querying;

public class QueryBuilderTests
{
    // Test entity matching ISyncEntity contract
    private class TestCustomer : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.Empty;
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int Age { get; set; }
        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "System";
        public bool IsDeleted { get; set; }
    }

    private static readonly HashSet<string> ValidColumns = new()
    {
        "Id", "Name", "Email", "Age", "IsDirty",
        "ModifiedAtUtc", "SyncSessionId", "ModifiedByUserId", "IsDeleted"
    };

    private QueryBuilder<TestCustomer> CreateBuilder() => new(ValidColumns);

    #region Comparison Operators

    [Fact]
    public void Where_Equals_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Name == "Acme")
            .ToDataQuery();

        query.Filters.Should().HaveCount(1);
        query.Filters![0].Column.Should().Be("Name");
        query.Filters[0].Operator.Should().Be(FilterOperator.Equals);
        query.Filters[0].Value.Should().Be("Acme");
    }

    [Fact]
    public void Where_NotEquals_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Name != "Deleted")
            .ToDataQuery();

        query.Filters.Should().HaveCount(1);
        query.Filters![0].Operator.Should().Be(FilterOperator.NotEquals);
    }

    [Fact]
    public void Where_GreaterThan_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Age > 18)
            .ToDataQuery();

        query.Filters.Should().HaveCount(1);
        query.Filters![0].Column.Should().Be("Age");
        query.Filters![0].Operator.Should().Be(FilterOperator.GreaterThan);
        query.Filters![0].Value.Should().Be(18);
    }

    [Fact]
    public void Where_LessThan_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Age < 65)
            .ToDataQuery();

        query.Filters![0].Operator.Should().Be(FilterOperator.LessThan);
    }

    [Fact]
    public void Where_GreaterThanOrEqual_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Age >= 21)
            .ToDataQuery();

        query.Filters![0].Operator.Should().Be(FilterOperator.GreaterThanOrEqual);
    }

    [Fact]
    public void Where_LessThanOrEqual_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Age <= 99)
            .ToDataQuery();

        query.Filters![0].Operator.Should().Be(FilterOperator.LessThanOrEqual);
    }

    [Fact]
    public void Where_ReversedComparison_FlipsOperator()
    {
        // 18 < c.Age  →  c.Age > 18
        var query = CreateBuilder()
            .Where(c => 18 < c.Age)
            .ToDataQuery();

        query.Filters![0].Column.Should().Be("Age");
        query.Filters![0].Operator.Should().Be(FilterOperator.GreaterThan);
        query.Filters![0].Value.Should().Be(18);
    }

    #endregion

    #region String Methods

    [Fact]
    public void Where_Contains_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Name.Contains("Corp"))
            .ToDataQuery();

        query.Filters.Should().HaveCount(1);
        query.Filters![0].Column.Should().Be("Name");
        query.Filters[0].Operator.Should().Be(FilterOperator.Contains);
        query.Filters[0].Value.Should().Be("Corp");
    }

    [Fact]
    public void Where_StartsWith_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Email.StartsWith("admin@"))
            .ToDataQuery();

        query.Filters![0].Column.Should().Be("Email");
        query.Filters![0].Operator.Should().Be(FilterOperator.StartsWith);
        query.Filters![0].Value.Should().Be("admin@");
    }

    #endregion

    #region In Operator

    [Fact]
    public void Where_InParams_ProducesCorrectFilter()
    {
        var query = CreateBuilder()
            .Where(c => c.Name.In("Acme", "Globex", "Initech"))
            .ToDataQuery();

        query.Filters.Should().HaveCount(1);
        query.Filters![0].Column.Should().Be("Name");
        query.Filters[0].Operator.Should().Be(FilterOperator.In);
        query.Filters[0].Value.Should().BeEquivalentTo(
            new List<object?> { "Acme", "Globex", "Initech" });
    }

    [Fact]
    public void Where_InCollection_ProducesCorrectFilter()
    {
        var names = new List<string> { "Alpha", "Beta" };
        var query = CreateBuilder()
            .Where(c => c.Name.In(names))
            .ToDataQuery();

        query.Filters![0].Operator.Should().Be(FilterOperator.In);
        query.Filters[0].Value.Should().BeEquivalentTo(
            new List<object?> { "Alpha", "Beta" });
    }

    #endregion

    #region Multiple Filters & Chaining

    [Fact]
    public void Where_MultipleCalls_CombinesFilters()
    {
        var query = CreateBuilder()
            .Where(c => c.Name.Contains("Corp"))
            .Where(c => c.Age > 18)
            .ToDataQuery();

        query.Filters.Should().HaveCount(2);
        query.Filters![0].Column.Should().Be("Name");
        query.Filters[1].Column.Should().Be("Age");
    }

    [Fact]
    public void Where_AndAlsoInSingleExpression_ProducesMultipleFilters()
    {
        var query = CreateBuilder()
            .Where(c => c.Age > 18 && c.Name != "Test")
            .ToDataQuery();

        query.Filters.Should().HaveCount(2);
        query.Filters![0].Column.Should().Be("Age");
        query.Filters[1].Column.Should().Be("Name");
    }

    [Fact]
    public void Where_CapturedVariable_EvaluatesCorrectly()
    {
        var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var query = CreateBuilder()
            .Where(c => c.ModifiedAtUtc > cutoff)
            .ToDataQuery();

        query.Filters![0].Value.Should().Be(cutoff);
    }

    [Fact]
    public void Where_NullComparison_ProducesEqualsNull()
    {
        var query = CreateBuilder()
            .Where(c => c.Name == null)
            .ToDataQuery();

        query.Filters![0].Operator.Should().Be(FilterOperator.Equals);
        query.Filters![0].Value.Should().BeNull();
    }

    #endregion

    #region Ordering, Pagination, IncludeDeleted

    [Fact]
    public void OrderByDescending_SetsCorrectly()
    {
        var query = CreateBuilder()
            .OrderByDescending(c => c.ModifiedAtUtc)
            .ToDataQuery();

        query.OrderBy.Should().Be("ModifiedAtUtc");
        query.OrderDescending.Should().BeTrue();
    }

    [Fact]
    public void OrderBy_SetsAscending()
    {
        var query = CreateBuilder()
            .OrderBy(c => c.Name)
            .ToDataQuery();

        query.OrderBy.Should().Be("Name");
        query.OrderDescending.Should().BeFalse();
    }

    [Fact]
    public void Skip_Take_SetsPagination()
    {
        var query = CreateBuilder()
            .Skip(20).Take(10)
            .ToDataQuery();

        query.Offset.Should().Be(20);
        query.Limit.Should().Be(10);
    }

    [Fact]
    public void IncludeDeleted_SetsFlag()
    {
        var query = CreateBuilder()
            .IncludeDeleted()
            .ToDataQuery();

        query.IncludeDeleted.Should().BeTrue();
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var query = CreateBuilder().ToDataQuery();

        query.Filters.Should().BeNull();
        query.Offset.Should().Be(0);
        query.Limit.Should().Be(50);
        query.OrderBy.Should().BeNull();
        query.OrderDescending.Should().BeTrue();
        query.IncludeDeleted.Should().BeFalse();
    }

    #endregion

    #region Error Cases

    [Fact]
    public void Where_InvalidColumn_ThrowsArgumentException()
    {
        // Use a property that's not in ValidColumns
        var builder = new QueryBuilder<TestCustomer>(
            new HashSet<string> { "Id", "Name" });

        var act = () => builder.Where(c => c.Email == "test@test.com");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown column 'Email'*");
    }

    [Fact]
    public void OrderBy_InvalidColumn_ThrowsArgumentException()
    {
        var builder = new QueryBuilder<TestCustomer>(
            new HashSet<string> { "Id", "Name" });

        var act = () => builder.OrderBy(c => c.Age);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown column 'Age'*");
    }

    [Fact]
    public void Where_UnsupportedMethod_ThrowsNotSupportedException()
    {
        var act = () => CreateBuilder()
            .Where(c => c.Name.EndsWith("Corp"));

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Unsupported method call*EndsWith*");
    }

    [Fact]
    public void Skip_Negative_ThrowsArgumentOutOfRange()
    {
        var act = () => CreateBuilder().Skip(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Take_Zero_ThrowsArgumentOutOfRange()
    {
        var act = () => CreateBuilder().Take(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Where_Null_ThrowsArgumentNull()
    {
        var act = () => CreateBuilder().Where(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Full Fluent Chain

    [Fact]
    public void FullChain_ProducesCompleteQuery()
    {
        var cutoff = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var query = CreateBuilder()
            .Where(c => c.Name.Contains("Corp"))
            .Where(c => c.ModifiedAtUtc > cutoff)
            .Where(c => c.Age >= 21)
            .OrderByDescending(c => c.ModifiedAtUtc)
            .Skip(10).Take(25)
            .IncludeDeleted()
            .ToDataQuery();

        query.Filters.Should().HaveCount(3);
        query.Offset.Should().Be(10);
        query.Limit.Should().Be(25);
        query.OrderBy.Should().Be("ModifiedAtUtc");
        query.OrderDescending.Should().BeTrue();
        query.IncludeDeleted.Should().BeTrue();
    }

    #endregion
}
