using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SyncSession.Client.Engine;
using SyncSession.Client.Services;
using SyncSession.Client.Utilities;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Exceptions;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Tests that <see cref="SyncCoordinator"/> forwards the per-call <see cref="SyncContext"/>
/// to the engine and applies its network gate consistently: full sync soft-fails when offline,
/// targeted push/pull throw <see cref="NetworkUnavailableException"/>, and all three honor
/// <c>requireNetwork</c>.
/// </summary>
public class SyncCoordinatorTests
{
    /// <summary>A NetworkHelper that reports offline (IsNetworkAvailable is virtual for this).</summary>
    private sealed class OfflineNetworkHelper : NetworkHelper
    {
        public override bool IsNetworkAvailable() => false;
    }

    private static (SyncCoordinator Coordinator, Mock<ISyncServerApi> ServerApi) BuildCoordinator(
        NetworkHelper? network = null)
    {
        var serverApi = new Mock<ISyncServerApi>();
        var clientDb = new Mock<IClientDatabase>();

        var config = new ClientSyncConfiguration { TenantId = Guid.NewGuid() };
        config.RegisterTable<CoordinatorTestEntity>("CoordinatorTest");

        var engine = ClientSyncEngineBuilder.Build(
            clientDb.Object, serverApi.Object, Guid.NewGuid(), config, typeof(CoordinatorTestEntity).Assembly);

        return (new SyncCoordinator(engine, network), serverApi);
    }

    private static void VerifyNoServerIO(Mock<ISyncServerApi> serverApi)
    {
        serverApi.Verify(s => s.BeginPushAsync(It.IsAny<List<TableSyncInfo>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_ForwardsContext_TenantMismatchFailsFastWithNoIO()
    {
        var (coordinator, serverApi) = BuildCoordinator();

        // requireNetwork:false skips the network check and goes straight to the engine.
        var act = () => coordinator.SyncAsync(
            requireNetwork: false,
            context: new SyncContext { ExpectedTenantId = Guid.NewGuid() });

        await act.Should().ThrowAsync<TenantMismatchException>();
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task SyncAsync_Offline_ReturnsFailedResult_NoEngineIO()
    {
        var (coordinator, serverApi) = BuildCoordinator(new OfflineNetworkHelper());

        var result = await coordinator.SyncAsync(requireNetwork: true);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network unavailable");
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task PushAsync_Offline_ThrowsNetworkUnavailable_NoIO()
    {
        var (coordinator, serverApi) = BuildCoordinator(new OfflineNetworkHelper());

        var act = () => coordinator.PushAsync(); // requireNetwork defaults true

        await act.Should().ThrowAsync<NetworkUnavailableException>();
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task PullAsync_Offline_ThrowsNetworkUnavailable_NoIO()
    {
        var (coordinator, serverApi) = BuildCoordinator(new OfflineNetworkHelper());

        var act = () => coordinator.PullAsync(); // requireNetwork defaults true

        await act.Should().ThrowAsync<NetworkUnavailableException>();
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task PushAsync_RequireNetworkFalse_BypassesGate_AndForwardsContext()
    {
        // Offline, but requireNetwork:false skips the network gate; the mismatched context
        // then proves Push forwarded it to the engine (which fails closed before any I/O).
        var (coordinator, serverApi) = BuildCoordinator(new OfflineNetworkHelper());

        var act = () => coordinator.PushAsync(
            requireNetwork: false,
            context: new SyncContext { ExpectedTenantId = Guid.NewGuid() });

        await act.Should().ThrowAsync<TenantMismatchException>();
        VerifyNoServerIO(serverApi);
    }
}

// Single-tenant test entity, no [SyncTable] (not auto-discovered); registered explicitly.
public class CoordinatorTestEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; }
}
