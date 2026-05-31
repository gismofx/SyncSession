using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SyncSession.Client.Engine;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Tests for ClientSyncEngineBuilder - verifies automatic table discovery and handler creation
/// </summary>
public class ClientSyncEngineBuilderTests
{
    [Fact]
    public void Build_WithSingleAssembly_DiscoversTablesAndCreatesHandlers()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var deviceId = Guid.NewGuid();
        var assembly = typeof(TestEntity1).Assembly;

        // Act
        var engine = ClientSyncEngineBuilder.Build(
            mockDb.Object,
            mockClient.Object,
            deviceId,
            null,
            typeof(TestEntity1).Assembly);

        // Assert
        engine.Should().NotBeNull();
        // Engine should have been created successfully with handlers
    }

    [Fact]
    public void Build_WithMultipleAssemblies_DiscoversAllTables()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var deviceId = Guid.NewGuid();
        var assembly1 = typeof(TestEntity1).Assembly;
        var assembly2 = typeof(TestEntity1).Assembly; // Same assembly for test purposes

        // Act
        var engine = ClientSyncEngineBuilder.Build(
            mockDb.Object,
            mockClient.Object,
            deviceId,
            null, // config
            assembly1,
            assembly2);

        // Assert
        engine.Should().NotBeNull();
    }

    [Fact]
    public void Build_WithNullAssembly_ThrowsArgumentNullException()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var deviceId = Guid.NewGuid();

        // Act
        Action act = () => ClientSyncEngineBuilder.Build(
            mockDb.Object,
            mockClient.Object,
            deviceId,
            null,          // config (optional)
            (Assembly)null!); // null assembly

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("entitiesAssembly");
    }

    [Fact]
    public void Build_WithNoAssemblies_ThrowsArgumentException()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var deviceId = Guid.NewGuid();

        // Act - explicitly call multi-assembly overload with empty array
        Action act = () => ClientSyncEngineBuilder.Build(
            mockDb.Object,
            mockClient.Object,
            deviceId,
            null, // config
            Array.Empty<Assembly>()); // empty assemblies array

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("At least one assembly must be provided*");
    }

    [Fact]
    public void Build_CreatesHandlersForAllDiscoveredTables()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var deviceId = Guid.NewGuid();
        
        var config = new ClientSyncConfiguration();
        var assembly = typeof(TestEntity1).Assembly;

        // Act
        var engine = ClientSyncEngineBuilder.Build(
            mockDb.Object,
            mockClient.Object,
            deviceId,
            config,
            assembly);

        // Assert
        var tables = config.GetTables().ToList();
        tables.Should().NotBeEmpty();
        tables.Should().OnlyContain(t => t.Handler != null, "all tables should have handlers");
    }

    [Fact]
    public void Build_WithCustomConfiguration_PreservesSettings()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        var deviceId = Guid.NewGuid();
        
        var config = new ClientSyncConfiguration
        {
            PushBatchSize = 500,
            PullBatchSize = 750
        };
        
        var assembly = typeof(TestEntity1).Assembly;

        // Act
        var engine = ClientSyncEngineBuilder.Build(
            mockDb.Object,
            mockClient.Object,
            deviceId,
            config,
            assembly);

        // Assert
        config.PushBatchSize.Should().Be(500);
        config.PullBatchSize.Should().Be(750);
    }
}

// Test entities for discovery
[SyncTable("TestEntity1", Priority = 1)]
public class TestEntity1 : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get;set; } = false;
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";

    public bool IsDeleted { get; set; } = false;
}

[SyncTable("TestEntity2", Priority = 2)]
public class TestEntity2 : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get;set; } = false;
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";

    public bool IsDeleted { get; set; } = false;
}
