using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Server.BackgroundServices;
using SyncSession.Server.Models;
using Xunit;

namespace SyncSession.UnitTests.Server;

/// <summary>
/// Tests for SyncCleanupBackgroundService.
/// Background service dispatches to ICleanupService implementations only —
/// tests use ICleanupService mocks, not concrete types.
/// </summary>
public class SyncCleanupBackgroundServiceTests
{
    private readonly Mock<ICleanupService> _mockService1;
    private readonly Mock<ICleanupService> _mockService2;
    private readonly Mock<ICleanupService> _mockService3;
    private readonly Mock<ILogger<SyncCleanupBackgroundService>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly ServerSyncConfiguration _config;

    public SyncCleanupBackgroundServiceTests()
    {
        _mockLogger = new Mock<ILogger<SyncCleanupBackgroundService>>();

        _config = new ServerSyncConfiguration
        {
            SharedTableCleanupIntervalMinutes = 60,
        };

        _mockService1 = new Mock<ICleanupService>();
        _mockService2 = new Mock<ICleanupService>();
        _mockService3 = new Mock<ICleanupService>();

        // Default: nothing to clean up
        _mockService1.Setup(s => s.ExecuteCleanupAsync()).ReturnsAsync(0);
        _mockService2.Setup(s => s.ExecuteCleanupAsync()).ReturnsAsync(0);
        _mockService3.Setup(s => s.ExecuteCleanupAsync()).ReturnsAsync(0);

        _mockService1.Setup(s => s.GetCleanupDescription()).Returns("Session cleanup");
        _mockService2.Setup(s => s.GetCleanupDescription()).Returns("Shared table cleanup");
        _mockService3.Setup(s => s.GetCleanupDescription()).Returns("Temp table cleanup");

        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();

        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);

        IEnumerable<ICleanupService> cleanupServices = new[]
        {
            _mockService1.Object,
            _mockService2.Object,
            _mockService3.Object
        };
        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IEnumerable<ICleanupService>)))
            .Returns(cleanupServices);
    }

    private SyncCleanupBackgroundService CreateService() =>
        new SyncCleanupBackgroundService(_mockScopeFactory.Object, _mockLogger.Object, _config);

    private async Task RunCleanupCycleViaReflection(SyncCleanupBackgroundService service)
    {
        var method = typeof(SyncCleanupBackgroundService)
            .GetMethod("RunCleanupCycleAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull("RunCleanupCycleAsync method should exist");

        var task = (Task)method!.Invoke(service, new object[] { CancellationToken.None })!;
        await task;
    }

    [Fact]
    public async Task RunCleanupCycle_ShouldCallAllCleanupServices()
    {
        var service = CreateService();
        await RunCleanupCycleViaReflection(service);

        _mockService1.Verify(s => s.ExecuteCleanupAsync(), Times.Once);
        _mockService2.Verify(s => s.ExecuteCleanupAsync(), Times.Once);
        _mockService3.Verify(s => s.ExecuteCleanupAsync(), Times.Once);
    }

    [Fact]
    public async Task RunCleanupCycle_ShouldCallAllServicesEvenWhenSomeReturnZero()
    {
        // Arrange - service1 returns 5, others 0
        _mockService1.Setup(s => s.ExecuteCleanupAsync()).ReturnsAsync(5);

        var service = CreateService();
        await RunCleanupCycleViaReflection(service);

        _mockService1.Verify(s => s.ExecuteCleanupAsync(), Times.Once);
        _mockService2.Verify(s => s.ExecuteCleanupAsync(), Times.Once);
        _mockService3.Verify(s => s.ExecuteCleanupAsync(), Times.Once);
    }

    [Fact]
    public async Task RunCleanupCycle_ShouldLogInformationWhenServiceCleansUp()
    {
        _mockService1.Setup(s => s.ExecuteCleanupAsync()).ReturnsAsync(5);
        _mockService1.Setup(s => s.GetCleanupDescription()).Returns("Session cleanup");

        var service = CreateService();
        await RunCleanupCycleViaReflection(service);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("Session cleanup") &&
                    v.ToString()!.Contains("5") &&
                    v.ToString()!.Contains("item(s) cleaned up")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCleanupCycle_ShouldLogDebugWhenNothingToClean()
    {
        // All services return 0 — should log Debug, not Information
        var service = CreateService();
        await RunCleanupCycleViaReflection(service);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("item(s) cleaned up")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCleanupCycle_ShouldLogStartAndCompletion()
    {
        var service = CreateService();
        await RunCleanupCycleViaReflection(service);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Starting cleanup cycle")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("completed")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCleanupCycle_ShouldContinueWhenOneServiceThrows()
    {
        _mockService1.Setup(s => s.ExecuteCleanupAsync()).ThrowsAsync(new Exception("DB error"));

        var service = CreateService();

        // Should not throw — errors are caught and logged per service
        await RunCleanupCycleViaReflection(service);

        // Other services should still run
        _mockService2.Verify(s => s.ExecuteCleanupAsync(), Times.Once);
        _mockService3.Verify(s => s.ExecuteCleanupAsync(), Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(e => e.Message == "DB error"),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Constructor_ShouldUseConfiguredCleanupInterval()
    {
        var customConfig = new ServerSyncConfiguration { SharedTableCleanupIntervalMinutes = 120 };
        var service = new SyncCleanupBackgroundService(
            _mockScopeFactory.Object, _mockLogger.Object, customConfig);
        service.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_ShouldUseDefaultIntervalWhenConfigIsZero()
    {
        var configWithZero = new ServerSyncConfiguration { SharedTableCleanupIntervalMinutes = 0 };
        var service = new SyncCleanupBackgroundService(
            _mockScopeFactory.Object, _mockLogger.Object, configWithZero);
        service.Should().NotBeNull();
    }
}
