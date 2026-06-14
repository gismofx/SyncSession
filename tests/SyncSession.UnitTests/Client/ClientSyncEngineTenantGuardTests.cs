using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SyncSession.Client.Engine;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Exceptions;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Tests for the per-call <see cref="SyncContext"/>: the fail-closed expected-tenant guard,
/// the null-tenant-on-multi-tenant construction guard, and the user display name override.
/// </summary>
public class ClientSyncEngineTenantGuardTests
{
    private readonly Guid _deviceId = Guid.NewGuid();

    /// <summary>
    /// Builds an engine over mocks. BeginPullAsync returns an empty response so PullAsync
    /// short-circuits right after the begin call — the clean assertion point for display name
    /// and "guard passed" behavior without needing per-table plumbing.
    /// </summary>
    private (ISyncEngine Engine, Mock<ISyncServerApi> ServerApi) BuildEngine(ClientSyncConfiguration config)
    {
        var serverApi = new Mock<ISyncServerApi>();
        var clientDb = new Mock<IClientDatabase>();

        serverApi.Setup(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
                 .ReturnsAsync(new PullSessionBeginResponse { PullSessionId = Guid.NewGuid() });
        serverApi.Setup(s => s.CompletePullAsync(It.IsAny<Guid>(), It.IsAny<List<Guid>>()))
                 .Returns(Task.CompletedTask);

        var engine = ClientSyncEngineBuilder.Build(
            clientDb.Object, serverApi.Object, _deviceId, config, typeof(TenantGuardSingleTenantEntity).Assembly);

        return (engine, serverApi);
    }

    private static ClientSyncConfiguration SingleTenantConfig(Guid? tenantId, string? userDisplayName = null)
    {
        var config = new ClientSyncConfiguration { TenantId = tenantId, UserDisplayName = userDisplayName };
        config.RegisterTable<TenantGuardSingleTenantEntity>("TenantGuardSingle");
        return config;
    }

    // -------------------------------------------------------------------------
    // Construction guard: multi-tenant tables require a configured tenant
    // -------------------------------------------------------------------------

    [Fact]
    public void Build_MultiTenantTable_NullTenant_Throws()
    {
        var config = new ClientSyncConfiguration(); // TenantId null
        config.RegisterTable<TenantGuardMultiTenantEntity>("TenantGuardMulti");

        Action act = () => ClientSyncEngineBuilder.Build(
            new Mock<IClientDatabase>().Object,
            new Mock<ISyncServerApi>().Object,
            _deviceId,
            config,
            typeof(TenantGuardMultiTenantEntity).Assembly);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*TenantId is null*multi-tenant*");
    }

    [Fact]
    public void Build_MultiTenantTable_TenantSet_Succeeds()
    {
        var config = new ClientSyncConfiguration { TenantId = Guid.NewGuid() };
        config.RegisterTable<TenantGuardMultiTenantEntity>("TenantGuardMulti");

        var engine = ClientSyncEngineBuilder.Build(
            new Mock<IClientDatabase>().Object,
            new Mock<ISyncServerApi>().Object,
            _deviceId,
            config,
            typeof(TenantGuardMultiTenantEntity).Assembly);

        engine.Should().NotBeNull();
    }

    [Fact]
    public void Build_SingleTenantTables_NullTenant_Succeeds()
    {
        // Only single-tenant tables registered + null tenant: the guard must NOT fire.
        var config = new ClientSyncConfiguration();
        config.RegisterTable<TenantGuardSingleTenantEntity>("TenantGuardSingle");

        var engine = ClientSyncEngineBuilder.Build(
            new Mock<IClientDatabase>().Object,
            new Mock<ISyncServerApi>().Object,
            _deviceId,
            config,
            typeof(TenantGuardSingleTenantEntity).Assembly);

        engine.Should().NotBeNull();
    }

    // -------------------------------------------------------------------------
    // Expected-tenant guard: fail closed, no I/O
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SynchronizeAsync_ExpectedTenantMismatch_ThrowsAndDoesNoIO()
    {
        var (engine, serverApi) = BuildEngine(SingleTenantConfig(Guid.NewGuid()));

        var act = () => engine.SynchronizeAsync(context: new SyncContext { ExpectedTenantId = Guid.NewGuid() });

        await act.Should().ThrowAsync<TenantMismatchException>();
        serverApi.Verify(s => s.BeginPushAsync(It.IsAny<List<TableSyncInfo>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task PushAsync_ExpectedTenantMismatch_Throws()
    {
        var (engine, serverApi) = BuildEngine(SingleTenantConfig(Guid.NewGuid()));

        var act = () => engine.PushAsync(context: new SyncContext { ExpectedTenantId = Guid.NewGuid() });

        await act.Should().ThrowAsync<TenantMismatchException>();
        serverApi.Verify(s => s.BeginPushAsync(It.IsAny<List<TableSyncInfo>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task PullAsync_ExpectedTenantMismatch_Throws()
    {
        var (engine, serverApi) = BuildEngine(SingleTenantConfig(Guid.NewGuid()));

        var act = () => engine.PullAsync(context: new SyncContext { ExpectedTenantId = Guid.NewGuid() });

        await act.Should().ThrowAsync<TenantMismatchException>();
        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task PullAsync_ExpectedTenantMatches_Proceeds()
    {
        var tenant = Guid.NewGuid();
        var (engine, serverApi) = BuildEngine(SingleTenantConfig(tenant));

        var act = () => engine.PullAsync(context: new SyncContext { ExpectedTenantId = tenant });

        await act.Should().NotThrowAsync();
        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task PullAsync_NullContext_NoGuard_Proceeds()
    {
        var (engine, serverApi) = BuildEngine(SingleTenantConfig(Guid.NewGuid()));

        var act = () => engine.PullAsync();

        await act.Should().NotThrowAsync();
        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task PullAsync_ContextWithNullExpectedTenant_NoGuard()
    {
        var (engine, _) = BuildEngine(SingleTenantConfig(Guid.NewGuid()));

        var act = () => engine.PullAsync(context: new SyncContext { UserDisplayName = "anyone" });

        await act.Should().NotThrowAsync();
    }

    // -------------------------------------------------------------------------
    // Per-call user display name override
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PullAsync_ContextUserDisplayName_OverridesConfig()
    {
        var (engine, serverApi) = BuildEngine(SingleTenantConfig(Guid.NewGuid(), userDisplayName: "config-user"));

        await engine.PullAsync(context: new SyncContext { UserDisplayName = "call-user" });

        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), "call-user"), Times.Once);
    }

    [Fact]
    public async Task PullAsync_NoContext_UsesConfigUserDisplayName()
    {
        var (engine, serverApi) = BuildEngine(SingleTenantConfig(Guid.NewGuid(), userDisplayName: "config-user"));

        await engine.PullAsync();

        serverApi.Verify(s => s.BeginPullAsync(It.IsAny<List<string>>(), It.IsAny<Guid?>(), "config-user"), Times.Once);
    }

    // -------------------------------------------------------------------------
    // Exception shape
    // -------------------------------------------------------------------------

    [Fact]
    public void TenantMismatchException_SetsConfiguredAndExpected_AndMessage()
    {
        var configured = Guid.NewGuid();
        var expected = Guid.NewGuid();

        var ex = new TenantMismatchException(configured, expected);

        ex.ConfiguredTenantId.Should().Be(configured);
        ex.ExpectedTenantId.Should().Be(expected);
        ex.Message.Should().Contain(configured.ToString()).And.Contain(expected.ToString());
    }
}

// Test entities — intentionally WITHOUT [SyncTable] so they are not auto-discovered by
// other tests' assembly scans; registered explicitly where used.
public class TenantGuardSingleTenantEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; }
}

public class TenantGuardMultiTenantEntity : IMultiTenantSyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public Guid TenantId { get; set; } = Guid.Empty;
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; }
}
