using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Utilities;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Samples.Shared.TestData;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// A device must not pull back the records it just pushed.
///
/// FindUnseenSessionIdsAsync excludes only sessions present in ClientProcessedSessions; it has no
/// DeviceId filter, and nothing records the pushing device as having "seen" its own session at
/// commit time. The device therefore re-downloads its own rows on the next pull and overwrites its
/// local copies (TableSyncHandler.PullAsync upserts unconditionally) — wasted bandwidth, and any
/// server-side value mutation silently clobbers good local data.
///
/// EXPECTED STATE: the self-pull test FAILS against current code (proves the omission).
/// The other-device test PASSES and must keep passing — session-based tracking must still work.
/// </summary>
[Collection("MariaDB Collection")]
public class PushSessionSelfPullExclusionTests
{
    private readonly TestDatabaseFactory _dbFactory;

    public PushSessionSelfPullExclusionTests(MariaDbFixture fixture)
    {
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    [Fact]
    public async Task PushingDevice_DoesNotSeeItsOwnSession_AsUnseen()
    {
        var (serverDb, tempTableManager, sessionTracker, queueProcessor, config) =
            await BuildStackAsync(nameof(PushingDevice_DoesNotSeeItsOwnSession_AsUnseen));

        var deviceId = Guid.NewGuid();
        await PushOneCustomerAsync(sessionTracker, tempTableManager, queueProcessor, config, deviceId);

        // The device that pushed already holds these records. It must have nothing to pull.
        var unseen = await serverDb.FindUnseenSessionIdsAsync(deviceId);

        unseen.Should().BeEmpty(
            "a device must not be handed back the records it just pushed");
    }

    [Fact]
    public async Task PushingDevice_PullBegin_ReturnsNoTables()
    {
        var (_, tempTableManager, sessionTracker, queueProcessor, config) =
            await BuildStackAsync(nameof(PushingDevice_PullBegin_ReturnsNoTables));

        var deviceId = Guid.NewGuid();
        await PushOneCustomerAsync(sessionTracker, tempTableManager, queueProcessor, config, deviceId);

        var pull = await sessionTracker.CreatePullSessionAsync(new PullSessionBeginRequest
        {
            DeviceId = deviceId,
            TableNames = new[] { "Customers" }
        });

        pull.Tables.Should().BeEmpty(
            "the pushing device has nothing unseen, so pull-begin must return no tables");
    }

    [Fact]
    public async Task OtherDevice_StillSeesTheSession_AsUnseen()
    {
        // Regression guard: session-based tracking must keep working for every other device.
        var (serverDb, tempTableManager, sessionTracker, queueProcessor, config) =
            await BuildStackAsync(nameof(OtherDevice_StillSeesTheSession_AsUnseen));

        var pushingDevice = Guid.NewGuid();
        var otherDevice = Guid.NewGuid();
        var sessionId = await PushOneCustomerAsync(
            sessionTracker, tempTableManager, queueProcessor, config, pushingDevice);

        var unseen = await serverDb.FindUnseenSessionIdsAsync(otherDevice);

        unseen.Should().Contain(sessionId,
            "other devices must still receive records pushed by this device");
    }

    [Fact]
    public async Task FailedSession_DoesNotMarkPushingDevice_AsProcessed()
    {
        // Marking must be atomic with the commit. If a session rolls back but the device was
        // recorded as having seen it, those records become permanently unreachable for that
        // device - a silent data loss strictly worse than the re-pull it replaces.
        var (_, tempTableManager, sessionTracker, queueProcessor, config) =
            await BuildStackAsync(nameof(FailedSession_DoesNotMarkPushingDevice_AsProcessed));

        var deviceId = Guid.NewGuid();

        var begin = new PushSessionBeginRequest
        {
            DeviceId = deviceId,
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config, new Dictionary<string, int> { ["Orders"] = 1 })
        };
        var sessionId = (await sessionTracker.CreatePushSessionAsync(begin)).SessionId;

        // Order referencing a non-existent Customer -> FK violation -> upsert transaction rolls back.
        var orphanOrder = EntityReflectionHelper.EntityToDictionary(
            TestDataGenerator.CreateOrderWithInvalidFK(Guid.NewGuid()));

        await tempTableManager.InsertBatchAsync(sessionId, "Orders", new List<Dictionary<string, object?>> { orphanOrder });
        await sessionTracker.CompleteTableAsync(sessionId, "Orders", totalRecordsSent: 1);
        await sessionTracker.MarkSessionReadyAsync(sessionId);

        try { await queueProcessor.ProcessReadySessionsAsync(default); }
        catch { /* expected - session fails and is marked Failed */ }

        using var connection = await _dbFactory.GetConnectionAsync();
        var marked = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ClientProcessedSessions WHERE DeviceId = @DeviceId AND SessionId = @SessionId",
            new { DeviceId = deviceId.ToString(), SessionId = sessionId.ToString() });

        marked.Should().Be(0,
            "a rolled-back session must never leave the device recorded as having seen its records");
    }

    // -- helpers --------------------------------------------------------------

    private async Task<(MySqlServerDatabase Db, TempTableManager Temp, SessionTracker Tracker,
        SyncQueueProcessor Queue, Server.Models.ServerSyncConfiguration Config)> BuildStackAsync(string testName)
    {
        var connectionString = await _dbFactory.CreateDatabaseAsync(testName);

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var metadataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, metadataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        return (serverDb, tempTableManager, sessionTracker, queueProcessor, config);
    }

    private static async Task<Guid> PushOneCustomerAsync(
        SessionTracker sessionTracker,
        TempTableManager tempTableManager,
        SyncQueueProcessor queueProcessor,
        Server.Models.ServerSyncConfiguration config,
        Guid deviceId)
    {
        var begin = new PushSessionBeginRequest
        {
            DeviceId = deviceId,
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config, new Dictionary<string, int> { ["Customers"] = 1 })
        };
        var sessionId = (await sessionTracker.CreatePushSessionAsync(begin)).SessionId;

        var batch = TestDataGenerator.CreateCustomersDict(1, "vet-user");
        await tempTableManager.InsertBatchAsync(sessionId, "Customers", batch);
        await sessionTracker.CompleteTableAsync(sessionId, "Customers", totalRecordsSent: 1);
        await sessionTracker.MarkSessionReadyAsync(sessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        return sessionId;
    }
}
