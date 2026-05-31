using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SyncSession.Client.Engine;
using SyncSession.Core.Attributes;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Unit tests for push commit status polling in ClientSyncEngine.
/// Verifies WaitForPushCommitAsync behavior across success, failure, timeout, and cancellation scenarios.
/// </summary>
public class PushStatusPollingTests
{
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds an engine via ClientSyncEngineBuilder with a stubbed dirty record so
    /// PushAsync runs all the way through to WaitForPushCommitAsync.
    /// </summary>
    private (ISyncEngine Engine, Mock<ISyncServerApi> ServerApi) BuildEngine(ClientSyncConfiguration config)
    {
        var serverApi = new Mock<ISyncServerApi>();
        var clientDb = new Mock<IClientDatabase>();

        // One dirty record to push so the push path doesn't short-circuit
        clientDb.Setup(db => db.GetDirtyRecordsAsync<PushPollingTestEntity>(It.IsAny<Guid?>()))
                .ReturnsAsync([new PushPollingTestEntity { IsDirty = true }]);

        // Server push plumbing — return our fixed sessionId
        serverApi.Setup(s => s.BeginPushAsync(It.IsAny<List<TableSyncInfo>>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
                 .ReturnsAsync(_sessionId);
        serverApi.Setup(s => s.PushBatchAsync<PushPollingTestEntity>(It.IsAny<Guid>(), It.IsAny<IEnumerable<PushPollingTestEntity>>()))
                 .Returns(Task.CompletedTask);
        serverApi.Setup(s => s.CompleteTableAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>()))
                 .Returns(Task.CompletedTask);
        serverApi.Setup(s => s.CompletePushAsync(It.IsAny<Guid>()))
                 .Returns(Task.CompletedTask);

        var engine = ClientSyncEngineBuilder.Build(
            clientDb.Object,
            serverApi.Object,
            _deviceId,
            config,
            typeof(PushPollingTestEntity).Assembly);

        return (engine, serverApi);
    }

    private ClientSyncConfiguration FastPollingConfig(int timeoutSeconds = 5) => new ClientSyncConfiguration
    {
        PushStatusPollIntervalMs = 100,
        PushStatusTimeoutSeconds = timeoutSeconds < 1 ? 1 : timeoutSeconds
    };

    private static PushSessionStatusResponse Committed(Guid sessionId) => new()
    {
        SessionId = sessionId, Status = "Committed", SyncVersion = 42,
        CreatedAtUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow, CommittedAtUtc = DateTime.UtcNow
    };

    private static PushSessionStatusResponse Processing(Guid sessionId) => new()
    {
        SessionId = sessionId, Status = "Processing",
        CreatedAtUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow
    };

    private static PushSessionStatusResponse Failed(Guid sessionId, string error) => new()
    {
        SessionId = sessionId, Status = "Failed", ErrorMessage = error,
        CreatedAtUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow
    };

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task WaitForPushCommit_CommittedOnFirstPoll_Completes()
    {
        var config = FastPollingConfig();
        var (engine, serverApi) = BuildEngine(config);

        serverApi.Setup(s => s.GetPushStatusAsync(It.IsAny<Guid>()))
                 .ReturnsAsync(Committed(_sessionId));

        await engine.PushAsync();

        serverApi.Verify(s => s.GetPushStatusAsync(It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task WaitForPushCommit_CommittedAfterRetries_ReportsPushWaitingPerPoll()
    {
        var config = FastPollingConfig();
        var (engine, serverApi) = BuildEngine(config);

        var callCount = 0;
        serverApi.Setup(s => s.GetPushStatusAsync(It.IsAny<Guid>()))
                 .ReturnsAsync(() =>
                 {
                     callCount++;
                     return callCount < 3 ? Processing(_sessionId) : Committed(_sessionId);
                 });

        var reports = new List<SyncProgress>();
        var progress = new SynchronousProgress<SyncProgress>(p => reports.Add(p));

        await engine.PushAsync(progress);

        serverApi.Verify(s => s.GetPushStatusAsync(It.IsAny<Guid>()), Times.Exactly(3));
        reports.Count(r => r.Phase == SyncPhase.PushWaiting).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task WaitForPushCommit_FailedStatus_ThrowsInvalidOperationException()
    {
        var config = FastPollingConfig();
        var (engine, serverApi) = BuildEngine(config);

        serverApi.Setup(s => s.GetPushStatusAsync(It.IsAny<Guid>()))
                 .ReturnsAsync(Failed(_sessionId, "Queue processor error"));

        var act = () => engine.PushAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
                  .WithMessage("*Push session failed on server*Queue processor error*");
    }

    [Fact]
    public async Task WaitForPushCommit_Timeout_ThrowsTimeoutException()
    {
        // Timeout of 1s with server always returning Processing — times out after ~10 polls
        var config = new ClientSyncConfiguration { PushStatusPollIntervalMs = 100, PushStatusTimeoutSeconds = 1 };
        var (engine, serverApi) = BuildEngine(config);

        serverApi.Setup(s => s.GetPushStatusAsync(It.IsAny<Guid>()))
                 .ReturnsAsync(Processing(_sessionId));

        var act = () => engine.PushAsync();
        await act.Should().ThrowAsync<TimeoutException>()
                  .WithMessage("*did not reach Committed*");
    }
}

// Isolated entity for these tests to avoid conflicts with other test entities
[SyncTable("PushPollingTestEntities", Priority = 99)]
public class PushPollingTestEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; }
}
