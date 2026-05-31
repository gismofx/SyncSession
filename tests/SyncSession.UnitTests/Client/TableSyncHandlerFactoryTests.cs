using System;
using FluentAssertions;
using Moq;
using SyncSession.Client.Handlers;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Tests for TableSyncHandlerFactory - verifies handler creation with minimal reflection
/// </summary>
public class TableSyncHandlerFactoryTests
{
    [Fact]
    public void CreateHandler_ValidTableConfig_CreatesTypedHandler()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var config = new ClientSyncConfiguration();
        
        var factory = new TableSyncHandlerFactory(mockDb.Object, mockClient.Object, config);
        
        var tableConfig = new TableConfig
        {
            EntityType = typeof(FactoryTestEntity),
            TableName = "FactoryTestEntities",
            Priority = 1
        };

        // Act
        var handler = factory.CreateHandler(tableConfig);

        // Assert
        handler.Should().NotBeNull();
        handler.Should().BeAssignableTo<ITableSyncHandler>();
    }

    [Fact]
    public void CreateHandler_NullTableConfig_ThrowsArgumentNullException()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var config = new ClientSyncConfiguration();
        
        var factory = new TableSyncHandlerFactory(mockDb.Object, mockClient.Object, config);

        // Act
        Action act = () => factory.CreateHandler(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("tableConfig");
    }

    [Fact]
    public void CreateHandler_NullEntityType_ThrowsArgumentException()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var config = new ClientSyncConfiguration();
        
        var factory = new TableSyncHandlerFactory(mockDb.Object, mockClient.Object, config);
        
        var tableConfig = new TableConfig
        {
            EntityType = null!,
            TableName = "TestTable"
        };

        // Act
        Action act = () => factory.CreateHandler(tableConfig);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*EntityType*");
    }

    [Fact]
    public void CreateHandler_InvalidEntityType_ThrowsInvalidOperationException()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var config = new ClientSyncConfiguration();
        
        var factory = new TableSyncHandlerFactory(mockDb.Object, mockClient.Object, config);
        
        var tableConfig = new TableConfig
        {
            EntityType = typeof(string), // String doesn't implement ISyncEntity
            TableName = "InvalidTable"
        };

        // Act
        Action act = () => factory.CreateHandler(tableConfig);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ISyncEntity*");
    }

    [Fact]
    public void Constructor_NullClientDatabase_ThrowsArgumentNullException()
    {
        // Arrange
        var mockClient = new Mock<ISyncServerApi>();
        var config = new ClientSyncConfiguration();

        // Act
        Action act = () => new TableSyncHandlerFactory(null!, mockClient.Object, config);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("clientDatabase");
    }

    [Fact]
    public void Constructor_NullServerClient_ThrowsArgumentNullException()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var config = new ClientSyncConfiguration();

        // Act
        Action act = () => new TableSyncHandlerFactory(mockDb.Object, null!, config);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("serverClient");
    }

    [Fact]
    public void Constructor_NullConfiguration_ThrowsArgumentNullException()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();

        // Act
        Action act = () => new TableSyncHandlerFactory(mockDb.Object, mockClient.Object, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("configuration");
    }

    [Fact]
    public void CreateHandler_MultipleEntities_CreatesCorrectHandlerTypes()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var config = new ClientSyncConfiguration();
        
        var factory = new TableSyncHandlerFactory(mockDb.Object, mockClient.Object, config);

        // Act - Create handlers for different entity types
        var handler1 = factory.CreateHandler(new TableConfig
        {
            EntityType = typeof(FactoryTestEntity),
            TableName = "FactoryTestEntities"
        });

        var handler2 = factory.CreateHandler(new TableConfig
        {
            EntityType = typeof(AnotherFactoryTestEntity),
            TableName = "AnotherFactoryTestEntities"
        });

        // Assert
        handler1.Should().NotBeNull();
        handler2.Should().NotBeNull();
        handler1.Should().NotBeSameAs(handler2);
    }
}

[SyncTable("FactoryTestEntity")]
public class FactoryTestEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get; set; } = false;
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; } = false;
}

[SyncTable("AnotherFactoryTestEntity")]
public class AnotherFactoryTestEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get; set; } = false;
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; } = false;

}
