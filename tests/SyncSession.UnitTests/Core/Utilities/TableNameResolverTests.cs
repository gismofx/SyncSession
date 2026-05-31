using System;
using FluentAssertions;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;
using Xunit;

namespace SyncSession.UnitTests.Core.Utilities;

public class TableNameResolverTests : IDisposable
{
    public void Dispose()
    {
        // Each test gets a clean cache — prevents cross-test contamination
        // from the static ConcurrentDictionary.
        TableNameResolver.ClearCache();
    }

    // ── Explicit table name ──────────────────────────────────────────────

    [Fact]
    public void GetTableName_ExplicitName_ReturnsAttributeValue()
    {
        var result = TableNameResolver.GetTableName<ExplicitNameEntity>();

        result.Should().Be("MyCustomTable");
    }

    [Fact]
    public void GetTableName_TypeOverload_ExplicitName_ReturnsAttributeValue()
    {
        var result = TableNameResolver.GetTableName(typeof(ExplicitNameEntity));

        result.Should().Be("MyCustomTable");
    }

    // ── Parameterless [SyncTable] — falls back to class name ─────────────

    [Fact]
    public void GetTableName_ParameterlessAttribute_ReturnsClassName()
    {
        var result = TableNameResolver.GetTableName<ParameterlessEntity>();

        result.Should().Be(nameof(ParameterlessEntity));
    }

    [Fact]
    public void GetTableName_TypeOverload_ParameterlessAttribute_ReturnsClassName()
    {
        var result = TableNameResolver.GetTableName(typeof(ParameterlessEntity));

        result.Should().Be(nameof(ParameterlessEntity));
    }

    // ── Missing attribute ────────────────────────────────────────────────

    [Fact]
    public void GetTableName_NoAttribute_ThrowsInvalidOperationException()
    {
        Action act = () => TableNameResolver.GetTableName<NoAttributeEntity>();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*must have [SyncTable] attribute*");
    }

    // ── Cache behavior ───────────────────────────────────────────────────

    [Fact]
    public void GetTableName_CalledTwice_ReturnsSameInstance()
    {
        var first = TableNameResolver.GetTableName<ExplicitNameEntity>();
        var second = TableNameResolver.GetTableName<ExplicitNameEntity>();

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void ClearCache_SubsequentCallStillResolves()
    {
        _ = TableNameResolver.GetTableName<ExplicitNameEntity>();
        TableNameResolver.ClearCache();

        var result = TableNameResolver.GetTableName<ExplicitNameEntity>();

        result.Should().Be("MyCustomTable");
    }
}

// ── Test entities ────────────────────────────────────────────────────────

[SyncTable("MyCustomTable")]
internal class ExplicitNameEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; }
}

[SyncTable]
internal class ParameterlessEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; }
}

internal class NoAttributeEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; }
}
