using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SyncSession.Client.Engine;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Exceptions;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Tests for the engine's persisted tenant-binding enforcement: a multi-tenant database is bound
/// to exactly one tenant (adopted on first sync, or rejected when missing per policy), and a sync
/// whose configured tenant differs from the bound tenant is rejected before any I/O. Single-tenant
/// configurations skip enforcement entirely.
/// Reuses TenantGuardMultiTenantEntity / TenantGuardSingleTenantEntity from the guard test file.
/// </summary>
public class ClientSyncEngineTenantBindingTests
{
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();

    /// <summary>
    /// Builds a multi-tenant engine over mocks. <paramref name="boundTenant"/> is what the metadata
    /// store returns for the binding key (null = unbound). PullAsync short-circuits right after
    /// BeginPull (empty tables) — a clean "binding passed, proceeded" assertion point.
    /// </summary>
    private (ISyncEngine Engine, Mock<IClientDatabase> Db, Mock<ISyncServerApi> ServerApi) BuildEngine(
        Guid tenant, string? boundTenant, TenantBindingPolicy policy = TenantBindingPolicy.Reject)
    {
        var serverApi = new Mock<ISyncServerApi>();
        var clientDb = new Mock<IClientDatabase>();

        clientDb.Setup(d => d.GetClientMetadataAsync(ClientMetadataKeys.BoundTenantId))
                .ReturnsAsync(boundTenant);

        serverApi.Setup(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
                 .ReturnsAsync(new PullSessionBeginResponse { PullSessionId = Guid.NewGuid() });
        serverApi.Setup(s => s.CompletePullAsync(It.IsAny<Guid>(), It.IsAny<List<Guid>>()))
                 .Returns(Task.CompletedTask);

        var config = new ClientSyncConfiguration { TenantId = tenant, TenantBindingPolicy = policy };
        config.RegisterTable<TenantGuardMultiTenantEntity>("TenantGuardMulti");

        var engine = ClientSyncEngineBuilder.Build(
            clientDb.Object, serverApi.Object, _deviceId, config, typeof(TenantGuardMultiTenantEntity).Assembly);

        return (engine, clientDb, serverApi);
    }

    private static void VerifyNoServerIO(Mock<ISyncServerApi> s)
    {
        s.Verify(x => x.BeginPushAsync(It.IsAny<List<TableSyncInfo>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
        s.Verify(x => x.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task NoBinding_Adopt_WritesBindingAndProceeds()
    {
        var (engine, db, serverApi) = BuildEngine(_tenant, boundTenant: null, TenantBindingPolicy.Adopt);

        await engine.PullAsync();

        db.Verify(d => d.SetClientMetadataAsync(ClientMetadataKeys.BoundTenantId, _tenant.ToString()), Times.Once);
        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task NoBinding_Reject_ThrowsBindingMissing_NoIO_NoWrite()
    {
        var (engine, db, serverApi) = BuildEngine(_tenant, boundTenant: null, TenantBindingPolicy.Reject);

        var act = () => engine.PullAsync();

        await act.Should().ThrowAsync<TenantBindingMissingException>();
        db.Verify(d => d.SetClientMetadataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task BindingMatches_ProceedsWithoutRebinding()
    {
        var (engine, db, serverApi) = BuildEngine(_tenant, boundTenant: _tenant.ToString());

        await engine.PullAsync();

        db.Verify(d => d.SetClientMetadataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task BindingMismatch_Reject_ThrowsTenantMismatch_NoIO()
    {
        var other = Guid.NewGuid();
        var (engine, db, serverApi) = BuildEngine(_tenant, boundTenant: other.ToString(), TenantBindingPolicy.Reject);

        var act = () => engine.PullAsync();

        await act.Should().ThrowAsync<TenantMismatchException>();
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task BindingMismatch_Adopt_StillThrows_DoesNotOverride()
    {
        // Adopt only fills a MISSING binding — a present, different binding is always rejected.
        var other = Guid.NewGuid();
        var (engine, db, serverApi) = BuildEngine(_tenant, boundTenant: other.ToString(), TenantBindingPolicy.Adopt);

        var act = () => engine.PullAsync();

        await act.Should().ThrowAsync<TenantMismatchException>();
        db.Verify(d => d.SetClientMetadataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task PushAsync_NoBinding_Reject_Throws_NoIO()
    {
        var (engine, _, serverApi) = BuildEngine(_tenant, boundTenant: null, TenantBindingPolicy.Reject);

        var act = () => engine.PushAsync();

        await act.Should().ThrowAsync<TenantBindingMissingException>();
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task SynchronizeAsync_BindingMismatch_Throws_NoIO()
    {
        var other = Guid.NewGuid();
        var (engine, _, serverApi) = BuildEngine(_tenant, boundTenant: other.ToString());

        var act = () => engine.SynchronizeAsync();

        await act.Should().ThrowAsync<TenantMismatchException>();
        VerifyNoServerIO(serverApi);
    }

    [Fact]
    public async Task SingleTenant_BindingSkipped_NoMetadataAccess()
    {
        var serverApi = new Mock<ISyncServerApi>();
        var clientDb = new Mock<IClientDatabase>();
        serverApi.Setup(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
                 .ReturnsAsync(new PullSessionBeginResponse { PullSessionId = Guid.NewGuid() });
        serverApi.Setup(s => s.CompletePullAsync(It.IsAny<Guid>(), It.IsAny<List<Guid>>()))
                 .Returns(Task.CompletedTask);

        var config = new ClientSyncConfiguration(); // no tenant, single-tenant entity
        config.RegisterTable<TenantGuardSingleTenantEntity>("TenantGuardSingle");
        var engine = ClientSyncEngineBuilder.Build(
            clientDb.Object, serverApi.Object, _deviceId, config, typeof(TenantGuardSingleTenantEntity).Assembly);

        await engine.PullAsync();

        clientDb.Verify(d => d.GetClientMetadataAsync(It.IsAny<string>()), Times.Never);
        clientDb.Verify(d => d.SetClientMetadataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
