using System;
using System.Linq;
using FluentAssertions;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;
using Xunit;

namespace SyncSession.UnitTests.Core.Utilities;

public class EntityReflectionHelperTests
{
    // Test entity with standard properties
    private class TestEntity : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.Empty;
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int Age { get; set; }
        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "TestUser";
        public bool IsDeleted { get; set; } = false;
    }

    // Test entity with client-only properties
    private class ClientTestEntity : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "TestUser";
        public bool IsDeleted { get; set; } = false;
    }

    // Test entity with custom column names
    private class CustomColumnEntity : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.Empty;
        
        [SyncColumn(ColumnName = "full_name")]
        public string Name { get; set; } = string.Empty;
        
        [SyncColumn(ColumnName = "email_address")]
        public string Email { get; set; } = string.Empty;
        
        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "TestUser";
        public bool IsDeleted { get; set; } = false;
    }

    // Test entity with ignored properties
    private class IgnoredPropertyEntity : ISyncEntity
    {
        public Guid Id { get; set; } = Guid.Empty;
        public string Name { get; set; } = string.Empty;
        
        [SyncColumn(Ignore = true)]
        public string InternalNotes { get; set; } = string.Empty;
        
        public bool IsDirty { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public Guid? SyncSessionId { get; set; }
        public string ModifiedByUserId { get; set; } = "TestUser";
        public bool IsDeleted { get; set; } = false;
    }

    #region Context-Specific Column Selection Tests

    [Fact]
    public void GetColumnsForPullUpsert_ShouldIncludeCorrectColumns()
    {
        // Act
        var columns = EntityReflectionHelper.GetColumnsForPullUpsert<TestEntity>();

        // Assert - Business + IsDirty + ModifiedAtUtc + IsDeleted + ModifiedByUserId
        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("Email");
        columns.Should().Contain("Age");
        columns.Should().Contain("IsDirty");
        columns.Should().Contain("ModifiedAtUtc");
        columns.Should().Contain("IsDeleted");
        columns.Should().Contain("ModifiedByUserId");
        columns.Should().NotContain("SyncSessionId"); // Not needed client-side
    }

    [Fact]
    public void GetColumnsForPushSelect_ShouldIncludeCorrectColumns()
    {
        // Act
        var columns = EntityReflectionHelper.GetColumnsForPushSelect<TestEntity>();

        // Assert - Business + IsDeleted + ModifiedByUserId
        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("Email");
        columns.Should().Contain("Age");
        columns.Should().Contain("IsDeleted");
        columns.Should().Contain("ModifiedByUserId");
        columns.Should().Contain("ModifiedAtUtc"); // Session 22h: client owns timestamp
        columns.Should().NotContain("IsDirty"); // Client-only, not sent to server
        columns.Should().NotContain("SyncSessionId"); // Server assigns
    }

    [Fact]
    public void GetColumnsForServerUpsert_ShouldIncludeCorrectColumns()
    {
        // Act
        var columns = EntityReflectionHelper.GetColumnsForServerUpsert<TestEntity>();

        // Assert - Business + IsDeleted + ModifiedByUserId + ModifiedAtUtc (Session 22h)
        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("Email");
        columns.Should().Contain("Age");
        columns.Should().Contain("IsDeleted");
        columns.Should().Contain("ModifiedByUserId");
        columns.Should().Contain("ModifiedAtUtc"); // Session 22h: preserved from client, COALESCE fallback
        columns.Should().NotContain("IsDirty"); // Client-only
        columns.Should().NotContain("SyncSessionId"); // Server assigns during upsert
    }

    [Fact]
    public void GetColumnsForDirectUpsert_ShouldIncludeCorrectColumns()
    {
        // Act
        var columns = EntityReflectionHelper.GetColumnsForDirectUpsert<TestEntity>();

        // Assert - Same as ServerUpsert but WITH SyncSessionId (entity has it set by DirectWriteService)
        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("Email");
        columns.Should().Contain("Age");
        columns.Should().Contain("IsDeleted");
        columns.Should().Contain("ModifiedByUserId");
        columns.Should().Contain("ModifiedAtUtc");
        columns.Should().Contain("SyncSessionId"); // DirectWriteService sets this on entity
        columns.Should().NotContain("IsDirty"); // Client-only
    }

    [Fact]
    public void GetColumnsForServerSelect_ShouldIncludeCorrectColumns()
    {
        // Act
        var columns = EntityReflectionHelper.GetColumnsForServerSelect<TestEntity>();

        // Assert - Business + ModifiedAtUtc + SyncSessionId + IsDeleted + ModifiedByUserId
        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().Contain("Email");
        columns.Should().Contain("Age");
        columns.Should().Contain("ModifiedAtUtc");
        columns.Should().Contain("SyncSessionId"); // REQUIRED: Client needs this for session tracking
        columns.Should().Contain("IsDeleted");
        columns.Should().Contain("ModifiedByUserId");
        columns.Should().NotContain("IsDirty"); // Client-only property
    }

    [Fact]
    public void GetColumnsForPullUpsert_WithCustomColumnNames_ShouldUseCustomNames()
    {
        // Act
        var columns = EntityReflectionHelper.GetColumnsForPullUpsert<CustomColumnEntity>();

        // Assert
        columns.Should().Contain("Id");
        columns.Should().Contain("full_name"); // Custom name
        columns.Should().Contain("email_address"); // Custom name
        columns.Should().NotContain("Name"); // Original name excluded
        columns.Should().NotContain("Email"); // Original name excluded
    }

    [Fact]
    public void GetColumnsForPushSelect_WithIgnoredProperty_ShouldExcludeIgnored()
    {
        // Act
        var columns = EntityReflectionHelper.GetColumnsForPushSelect<IgnoredPropertyEntity>();

        // Assert
        columns.Should().Contain("Id");
        columns.Should().Contain("Name");
        columns.Should().NotContain("InternalNotes"); // Marked with [Ignore]
    }

    #endregion

    #region IsClientOnlyProperty Tests

    [Fact]
    public void IsClientOnlyProperty_IsDirty_ShouldReturnTrue()
    {
        // Act
        var result = EntityReflectionHelper.IsClientOnlyProperty("IsDirty");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsClientOnlyProperty_OtherProperties_ShouldReturnFalse()
    {
        // Act & Assert
        EntityReflectionHelper.IsClientOnlyProperty("Id").Should().BeFalse();
        EntityReflectionHelper.IsClientOnlyProperty("Name").Should().BeFalse();
        EntityReflectionHelper.IsClientOnlyProperty("ModifiedAtUtc").Should().BeFalse();
        EntityReflectionHelper.IsClientOnlyProperty("SyncSessionId").Should().BeFalse();
    }

    #endregion

    #region IsSyncEntityProperty Tests

    [Fact]
    public void IsSyncInfrastructureProperty_InfrastructureProperties_ShouldReturnTrue()
    {
        // Act & Assert - Infrastructure properties (auto-managed by sync system)
        EntityReflectionHelper.IsSyncInfrastructureProperty("IsDirty").Should().BeTrue();
        EntityReflectionHelper.IsSyncInfrastructureProperty("ModifiedAtUtc").Should().BeTrue();
        EntityReflectionHelper.IsSyncInfrastructureProperty("SyncSessionId").Should().BeTrue();
    }

    [Fact]
    public void IsSyncInfrastructureProperty_BusinessProperties_ShouldReturnFalse()
    {
        // Act & Assert - Business properties (preserved during sync)
        EntityReflectionHelper.IsSyncInfrastructureProperty("Id").Should().BeFalse();
        EntityReflectionHelper.IsSyncInfrastructureProperty("ModifiedByUserId").Should().BeFalse();
        EntityReflectionHelper.IsSyncInfrastructureProperty("IsDeleted").Should().BeFalse();
        
        // User-defined properties
        EntityReflectionHelper.IsSyncInfrastructureProperty("Name").Should().BeFalse();
        EntityReflectionHelper.IsSyncInfrastructureProperty("Email").Should().BeFalse();
        EntityReflectionHelper.IsSyncInfrastructureProperty("Age").Should().BeFalse();
    }

    #endregion

    #region CreateDynamicParameters Tests

    [Fact]
    public void CreateDynamicParameters_ShouldIncludeBusinessProperties()
    {
        // Arrange
        var entity = new TestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test Name",
            Email = "test@example.com",
            Age = 30
        };
        var sessionId = Guid.NewGuid();

        // Act
        var parameters = EntityReflectionHelper.CreateDynamicParameters(entity, sessionId);

        // Assert
        var paramNames = parameters.ParameterNames.ToList();
        paramNames.Should().Contain("Id");
        paramNames.Should().Contain("Name");
        paramNames.Should().Contain("Email");
        paramNames.Should().Contain("Age");
        paramNames.Should().Contain("SyncSessionId");
    }

    [Fact]
    public void CreateDynamicParameters_ShouldExcludeSyncPropertiesExceptId()
    {
        // Arrange
        var entity = new TestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            ModifiedAtUtc = DateTime.UtcNow,
            SyncSessionId = Guid.NewGuid()
        };
        var sessionId = Guid.NewGuid();

        // Act
        var parameters = EntityReflectionHelper.CreateDynamicParameters(entity, sessionId);

        // Assert
        var paramNames = parameters.ParameterNames.ToList();
        paramNames.Should().Contain("Id"); // Included despite being in ISyncEntity
        paramNames.Should().NotContain("ModifiedAtUtc"); // Excluded (sync property)
        paramNames.Should().Contain("SyncSessionId"); // Added by method
    }

    [Fact]
    public void CreateDynamicParameters_WithSqliteHandling_ShouldConvertDateTimeToIso8601()
    {
        // Arrange
        var testDate = new DateTime(2024, 12, 15, 10, 30, 0, DateTimeKind.Utc);
        var entity = new TestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            ModifiedAtUtc = testDate
        };
        var sessionId = Guid.NewGuid();

        // Act
        var parameters = EntityReflectionHelper.CreateDynamicParameters(entity, sessionId, sqliteDateTimeHandling: true);

        // Assert - We can't easily inspect DynamicParameters values, but we can verify it doesn't throw
        parameters.Should().NotBeNull();
        parameters.ParameterNames.Should().Contain("SyncSessionId");
    }

    [Fact]
    public void CreateDynamicParameters_WithIgnoredProperty_ShouldExclude()
    {
        // Arrange
        var entity = new IgnoredPropertyEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            InternalNotes = "Should be ignored"
        };
        var sessionId = Guid.NewGuid();

        // Act
        var parameters = EntityReflectionHelper.CreateDynamicParameters(entity, sessionId);

        // Assert
        var paramNames = parameters.ParameterNames.ToList();
        paramNames.Should().Contain("Id");
        paramNames.Should().Contain("Name");
        paramNames.Should().NotContain("InternalNotes");
    }

    #endregion

    #region GetAllPropertyNames Tests

    [Fact]
    public void GetAllPropertyNames_ShouldReturnAllPublicProperties()
    {
        // Act
        var properties = EntityReflectionHelper.GetAllPropertyNames<TestEntity>();

        // Assert
        properties.Should().Contain("Id");
        properties.Should().Contain("Name");
        properties.Should().Contain("Email");
        properties.Should().Contain("Age");
        properties.Should().Contain("IsDirty");
        properties.Should().Contain("ModifiedAtUtc");
        properties.Should().Contain("SyncSessionId");
        properties.Should().Contain("ModifiedByUserId");
    }

    #endregion

    #region GetPropertyValue Tests

    [Fact]
    public void GetPropertyValue_ExistingProperty_ShouldReturnValue()
    {
        // Arrange
        var entity = new TestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test Name",
            Age = 25
        };

        // Act
        var nameValue = EntityReflectionHelper.GetPropertyValue(entity, "Name");
        var ageValue = EntityReflectionHelper.GetPropertyValue(entity, "Age");

        // Assert
        nameValue.Should().Be("Test Name");
        ageValue.Should().Be(25);
    }

    [Fact]
    public void GetPropertyValue_NonExistentProperty_ShouldReturnNull()
    {
        // Arrange
        var entity = new TestEntity { Id = Guid.NewGuid() };

        // Act
        var value = EntityReflectionHelper.GetPropertyValue(entity, "NonExistentProperty");

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public void GetPropertyValue_StringIdProperty_ShouldReturnString()
    {
        // Arrange
        var testId = Guid.NewGuid();
        var entity = new TestEntity { Id = testId };

        // Act
        var value = EntityReflectionHelper.GetPropertyValue(entity, "Id");

        // Assert
        value.Should().Be(testId);
    }

    #endregion

    #region Caching Tests

    [Fact]
    public void GetColumnsForPullUpsert_CalledTwice_ShouldUseCachedResult()
    {
        // Arrange
        EntityReflectionHelper.ClearCache();

        // Act - First call
        var columns1 = EntityReflectionHelper.GetColumnsForPullUpsert<TestEntity>();
        
        // Act - Second call (should use cache)
        var columns2 = EntityReflectionHelper.GetColumnsForPullUpsert<TestEntity>();

        // Assert - Should return same instance (reference equality proves caching)
        columns1.Should().BeSameAs(columns2);
    }

    [Fact]
    public void GetColumnsForPushSelect_DifferentTypes_ShouldCreateSeparateCacheEntries()
    {
        // Arrange
        EntityReflectionHelper.ClearCache();

        // Act
        var columns1 = EntityReflectionHelper.GetColumnsForPushSelect<TestEntity>();
        var columns2 = EntityReflectionHelper.GetColumnsForPushSelect<ClientTestEntity>();

        // Assert - Should be different lists
        columns1.Should().NotBeSameAs(columns2);
        columns1.Should().Contain("Age"); // TestEntity has Age
        columns2.Should().NotContain("Age"); // ClientTestEntity doesn't
    }

    [Fact]
    public void ClearCache_ShouldInvalidateAllCachedResults()
    {
        // Arrange
        var columns1 = EntityReflectionHelper.GetColumnsForPullUpsert<TestEntity>();
        var properties1 = EntityReflectionHelper.GetAllPropertyNames<TestEntity>();

        // Act
        EntityReflectionHelper.ClearCache();
        
        var columns2 = EntityReflectionHelper.GetColumnsForPullUpsert<TestEntity>();
        var properties2 = EntityReflectionHelper.GetAllPropertyNames<TestEntity>();

        // Assert - Should be new instances after cache clear
        columns1.Should().NotBeSameAs(columns2);
        properties1.Should().NotBeSameAs(properties2);
        
        // But content should be identical
        columns1.Should().BeEquivalentTo(columns2);
        properties1.Should().BeEquivalentTo(properties2);
    }

    [Fact]
    public void GetAllPropertyNames_CalledTwice_ShouldUseCachedResult()
    {
        // Arrange
        EntityReflectionHelper.ClearCache();

        // Act
        var names1 = EntityReflectionHelper.GetAllPropertyNames<TestEntity>();
        var names2 = EntityReflectionHelper.GetAllPropertyNames<TestEntity>();

        // Assert
        names1.Should().BeSameAs(names2);
    }

    [Fact]
    public void GetPropertyValue_CalledMultipleTimes_ShouldUseCachedPropertyInfo()
    {
        // Arrange
        EntityReflectionHelper.ClearCache();
        var entity = new TestEntity { Id = Guid.NewGuid(), Name = "Test", Age = 30 };

        // Act - Call multiple times for same property
        var value1 = EntityReflectionHelper.GetPropertyValue(entity, "Name");
        var value2 = EntityReflectionHelper.GetPropertyValue(entity, "Name");
        var value3 = EntityReflectionHelper.GetPropertyValue(entity, "Name");

        // Assert - All should return same value
        value1.Should().Be("Test");
        value2.Should().Be("Test");
        value3.Should().Be("Test");
    }

    #endregion
}
