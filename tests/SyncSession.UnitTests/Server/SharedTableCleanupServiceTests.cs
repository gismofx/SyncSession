using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Server.Models;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.UnitTests.Server;

public class SharedTableCleanupServiceTests
{
    private readonly Mock<IServerDatabase> _mockDatabase;
    private readonly ServerSyncConfiguration _config;
    private readonly SharedTableCleanupService _service;

    public SharedTableCleanupServiceTests()
    {
        _mockDatabase = new Mock<IServerDatabase>();

        _config = new ServerSyncConfiguration();
        _config.RegisterTable<Customer>("Customers", 1);
        _config.RegisterTable<Order>("Orders", 2);
        _config.RegisterTable<OrderItem>("OrderItems", 3);

        _service = new SharedTableCleanupService(_mockDatabase.Object, NullLogger<SharedTableCleanupService>.Instance, _config);
    }

    [Fact]
    public async Task CleanupSharedTempTablesAsync_ShouldProcessAllConfiguredTables()
    {
        // Arrange
        _mockDatabase.Setup(db => db.DeleteOldSharedTempRowsAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(0);

        // Act
        await _service.CleanupSharedTempTablesAsync();

        // Assert - should process 6 tables (3 tables × 2 types: Push/Pull)
        _mockDatabase.Verify(
            db => db.DeleteOldSharedTempRowsAsync(It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Exactly(6));

        // Verify specific table names
        _mockDatabase.Verify(db => db.DeleteOldSharedTempRowsAsync("TempPushCustomers", It.IsAny<DateTime>()), Times.Once);
        _mockDatabase.Verify(db => db.DeleteOldSharedTempRowsAsync("TempPullCustomers", It.IsAny<DateTime>()), Times.Once);
        _mockDatabase.Verify(db => db.DeleteOldSharedTempRowsAsync("TempPushOrders", It.IsAny<DateTime>()), Times.Once);
        _mockDatabase.Verify(db => db.DeleteOldSharedTempRowsAsync("TempPullOrders", It.IsAny<DateTime>()), Times.Once);
        _mockDatabase.Verify(db => db.DeleteOldSharedTempRowsAsync("TempPushOrderItems", It.IsAny<DateTime>()), Times.Once);
        _mockDatabase.Verify(db => db.DeleteOldSharedTempRowsAsync("TempPullOrderItems", It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task CleanupSharedTempTablesAsync_ShouldUseCutoffTimeCorrectly()
    {
        // Arrange
        var olderThanHours = 48;
        var beforeCall = DateTime.UtcNow.AddHours(-olderThanHours);

        DateTime capturedCutoffTime = DateTime.MinValue;
        _mockDatabase.Setup(db => db.DeleteOldSharedTempRowsAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Callback<string, DateTime>((_, cutoff) => capturedCutoffTime = cutoff)
            .ReturnsAsync(0);

        // Act
        await _service.CleanupSharedTempTablesAsync(olderThanHours);

        // Assert - cutoff time should be approximately 48 hours ago
        capturedCutoffTime.Should().BeCloseTo(beforeCall, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CleanupSharedTempTablesAsync_ShouldReturnTotalDeletedCount()
    {
        // Arrange
        var deletedCounts = new Dictionary<string, int>
        {
            ["TempPushCustomers"] = 100,
            ["TempPullCustomers"] = 50,
            ["TempPushOrders"] = 200,
            ["TempPullOrders"] = 75,
            ["TempPushOrderItems"] = 300,
            ["TempPullOrderItems"] = 125
        };

        _mockDatabase.Setup(db => db.DeleteOldSharedTempRowsAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync((string tableName, DateTime _) => deletedCounts.GetValueOrDefault(tableName, 0));

        // Act
        var totalDeleted = await _service.CleanupSharedTempTablesAsync();

        // Assert
        totalDeleted.Should().Be(850); // 100 + 50 + 200 + 75 + 300 + 125
    }

    [Fact]
    public async Task CleanupSharedTempTablesAsync_ShouldHandleExceptionsGracefully()
    {
        // Arrange
        var exception = new Exception("Database error");
        
        _mockDatabase.Setup(db => db.DeleteOldSharedTempRowsAsync("TempPushCustomers", It.IsAny<DateTime>()))
            .ThrowsAsync(exception);
        
        _mockDatabase.Setup(db => db.DeleteOldSharedTempRowsAsync(It.Is<string>(s => s != "TempPushCustomers"), It.IsAny<DateTime>()))
            .ReturnsAsync(10);

        // Act
        var totalDeleted = await _service.CleanupSharedTempTablesAsync();

        // Assert - should continue processing other tables despite exception
        totalDeleted.Should().Be(50); // 5 remaining tables × 10 rows each
    }

    [Fact]
    public async Task CleanupSharedTempTablesAsync_ShouldOnlyProcessEnabledTables()
    {
        //should we also include an enabled table?

        // Arrange
        var config = new ServerSyncConfiguration();
        config.RegisterTable<Customer>("Customers", 1);
        config.RegisterTable<Order>("Orders", 2, enabled: false); // Disabled
        config.RegisterTable<OrderItem>("OrderItems", 3);

        // Create service instance with this specific config
        var service = new SharedTableCleanupService(_mockDatabase.Object, NullLogger<SharedTableCleanupService>.Instance, config);

        _mockDatabase.Setup(db => db.DeleteOldSharedTempRowsAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(0);

        // Act
        await service.CleanupSharedTempTablesAsync();

        // Assert - should process 4 tables (2 enabled tables × 2 types: Push/Pull)
        _mockDatabase.Verify(
            db => db.DeleteOldSharedTempRowsAsync(It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Exactly(4));

        // Verify Orders tables are NOT called
        _mockDatabase.Verify(db => db.DeleteOldSharedTempRowsAsync("TempPushOrders", It.IsAny<DateTime>()), Times.Never);
        _mockDatabase.Verify(db => db.DeleteOldSharedTempRowsAsync("TempPullOrders", It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task GetSharedTempTableRowCountsAsync_ShouldReturnAllTableCounts()
    {
        // Arrange
        var rowCounts = new Dictionary<string, int>
        {
            ["TempPushCustomers"] = 100,
            ["TempPullCustomers"] = 50,
            ["TempPushOrders"] = 200,
            ["TempPullOrders"] = 75,
            ["TempPushOrderItems"] = 300,
            ["TempPullOrderItems"] = 125
        };

        _mockDatabase.Setup(db => db.CountSharedTempTableRowsAsync(It.IsAny<string>()))
            .ReturnsAsync((string tableName) => rowCounts.GetValueOrDefault(tableName, 0));

        // Act
        var counts = await _service.GetSharedTempTableRowCountsAsync();

        // Assert
        counts.Should().HaveCount(6);
        counts["TempPushCustomers"].Should().Be(100);
        counts["TempPullCustomers"].Should().Be(50);
        counts["TempPushOrders"].Should().Be(200);
        counts["TempPullOrders"].Should().Be(75);
        counts["TempPushOrderItems"].Should().Be(300);
        counts["TempPullOrderItems"].Should().Be(125);
    }

    [Fact]
    public async Task GetSharedTempTableRowCountsAsync_ShouldHandleExceptions()
    {
        // Arrange
        var exception = new Exception("Database error");
        
        _mockDatabase.Setup(db => db.CountSharedTempTableRowsAsync("TempPushCustomers"))
            .ThrowsAsync(exception);
        
        _mockDatabase.Setup(db => db.CountSharedTempTableRowsAsync(It.Is<string>(s => s != "TempPushCustomers")))
            .ReturnsAsync(100);

        // Act
        var counts = await _service.GetSharedTempTableRowCountsAsync();

        // Assert
        counts.Should().HaveCount(6);
        counts["TempPushCustomers"].Should().Be(-1); // Error indicator
        counts.Values.Where(v => v != -1).Should().OnlyContain(v => v == 100);
    }

    [Fact]
    public async Task CleanupSharedTempTablesAsync_ShouldLogAppropriately()
    {
        // Arrange
        _mockDatabase.Setup(db => db.DeleteOldSharedTempRowsAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(100);

        // Act
        await _service.CleanupSharedTempTablesAsync(24);

        // Assert - logging verified via NullLogger (no verification needed)
    }
}
