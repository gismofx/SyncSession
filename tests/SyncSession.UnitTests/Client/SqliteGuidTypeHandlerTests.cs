using System;
using System.Data;
using FluentAssertions;
using Moq;
using SyncSession.Client.Database;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Session 19i: Tests for SqliteGuidTypeHandler and SqliteNullableGuidTypeHandler.
/// Verifies Guid ↔ SQLite TEXT round-tripping through Dapper type handlers.
/// </summary>
public class SqliteGuidTypeHandlerTests
{
    private readonly SqliteGuidTypeHandler _guidHandler = new();
    private readonly SqliteNullableGuidTypeHandler _nullableHandler = new();

    #region SqliteGuidTypeHandler.Parse

    [Fact]
    public void GuidHandler_Parse_ValidGuidString_ReturnsGuid()
    {
        var expected = Guid.NewGuid();
        var result = _guidHandler.Parse(expected.ToString());
        result.Should().Be(expected);
    }

    [Fact]
    public void GuidHandler_Parse_UpperCaseGuidString_ReturnsGuid()
    {
        var expected = Guid.NewGuid();
        var result = _guidHandler.Parse(expected.ToString().ToUpperInvariant());
        result.Should().Be(expected);
    }

    [Fact]
    public void GuidHandler_Parse_InvalidString_ThrowsFormatException()
    {
        var act = () => _guidHandler.Parse("not-a-guid");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void GuidHandler_Parse_EmptyString_ThrowsFormatException()
    {
        var act = () => _guidHandler.Parse(string.Empty);
        act.Should().Throw<FormatException>();
    }

    #endregion

    #region SqliteGuidTypeHandler.SetValue

    [Fact]
    public void GuidHandler_SetValue_SetsStringValueAndDbType()
    {
        var guid = Guid.NewGuid();
        var mockParam = new Mock<IDbDataParameter>();

        _guidHandler.SetValue(mockParam.Object, guid);

        mockParam.VerifySet(p => p.Value = guid.ToString());
        mockParam.VerifySet(p => p.DbType = DbType.String);
    }

    [Fact]
    public void GuidHandler_SetValue_EmptyGuid_SetsEmptyGuidString()
    {
        var mockParam = new Mock<IDbDataParameter>();

        _guidHandler.SetValue(mockParam.Object, Guid.Empty);

        mockParam.VerifySet(p => p.Value = Guid.Empty.ToString());
        mockParam.VerifySet(p => p.DbType = DbType.String);
    }

    #endregion

    #region SqliteNullableGuidTypeHandler.Parse

    [Fact]
    public void NullableHandler_Parse_ValidGuidString_ReturnsGuid()
    {
        var expected = Guid.NewGuid();
        var result = _nullableHandler.Parse(expected.ToString());
        result.Should().Be(expected);
    }

    [Fact]
    public void NullableHandler_Parse_Null_ReturnsNull()
    {
        var result = _nullableHandler.Parse(null!);
        result.Should().BeNull();
    }

    [Fact]
    public void NullableHandler_Parse_DBNull_ReturnsNull()
    {
        var result = _nullableHandler.Parse(DBNull.Value);
        result.Should().BeNull();
    }

    [Fact]
    public void NullableHandler_Parse_InvalidString_ThrowsFormatException()
    {
        var act = () => _nullableHandler.Parse("not-a-guid");
        act.Should().Throw<FormatException>();
    }

    #endregion

    #region SqliteNullableGuidTypeHandler.SetValue

    [Fact]
    public void NullableHandler_SetValue_WithGuid_SetsStringValue()
    {
        var guid = Guid.NewGuid();
        var mockParam = new Mock<IDbDataParameter>();

        _nullableHandler.SetValue(mockParam.Object, guid);

        mockParam.VerifySet(p => p.Value = guid.ToString());
        mockParam.VerifySet(p => p.DbType = DbType.String);
    }

    [Fact]
    public void NullableHandler_SetValue_Null_SetsDBNull()
    {
        var mockParam = new Mock<IDbDataParameter>();

        _nullableHandler.SetValue(mockParam.Object, null);

        mockParam.VerifySet(p => p.Value = DBNull.Value);
    }

    #endregion

    #region Round-Trip Consistency

    [Fact]
    public void GuidHandler_RoundTrip_ParseMatchesSetValue()
    {
        var original = Guid.NewGuid();

        // Simulate: SetValue stores as string, Parse reads it back
        var stored = original.ToString();
        var parsed = _guidHandler.Parse(stored);

        parsed.Should().Be(original);
    }

    [Fact]
    public void NullableHandler_RoundTrip_GuidPreserved()
    {
        Guid? original = Guid.NewGuid();

        var stored = original.Value.ToString();
        var parsed = _nullableHandler.Parse(stored);

        parsed.Should().Be(original);
    }

    [Fact]
    public void NullableHandler_RoundTrip_NullPreserved()
    {
        var parsed = _nullableHandler.Parse(DBNull.Value);
        parsed.Should().BeNull();
    }

    #endregion
}
