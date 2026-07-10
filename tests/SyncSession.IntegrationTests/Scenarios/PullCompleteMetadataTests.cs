using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Client.Http;
using SyncSession.Core.DTOs.Push;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Samples.Shared.TestData;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// The client must report the pull session's temp-table metadata at pull/complete.
///
/// HttpSyncServerApi.CompletePullAsync builds PullSessionCompleteRequest with only PullSessionId,
/// DeviceId and ProcessedSessionIds - it never sets Tables, which defaults to an empty dictionary
/// ([Required] passes: an empty dictionary is non-null). The server then:
///
///   var totalRows = request.Tables.Values.Sum(t => t.TotalRecords ?? 0);   -> always 0
///   await _tempTableManager.CleanupPullSessionAsync(id, request.Tables.Values);  -> foreach over nothing
///
/// So the pull row count in the sync log is structurally always zero, and per-session pull temp
/// data is never cleaned up. The metadata the client needs is exactly what pull/begin returned.
///
/// EXPECTED STATE: both tests FAIL against current code.
/// </summary>
[Collection("MariaDB Collection")]
public class PullCompleteMetadataTests
{
    private readonly TestDatabaseFactory _dbFactory;

    public PullCompleteMetadataTests(MariaDbFixture fixture)
    {
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    [Fact]
    public async Task PullComplete_RecordsActualRowCount_OnPullSession()
    {
        var (connectionString, factory, api, pullingDevice) = await ArrangePulledDataAsync(
            nameof(PullComplete_RecordsActualRowCount_OnPullSession));
        using var _ = factory;

        var (pullSessionId, pulled, sessionIds) = await RunPullAsync(api);
        pulled.Should().Be(3, "three customers were pushed by the other device");

        await api.CompletePullAsync(pullSessionId, sessionIds);

        using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var totalRows = await connection.ExecuteScalarAsync<int>(
            "SELECT TotalRows FROM SessionRecords WHERE SessionId = @Id",
            new { Id = pullSessionId.ToString() });

        totalRows.Should().Be(3,
            "the pull session must record how many rows the device actually pulled");
    }

    [Fact]
    public async Task PullComplete_CleansUpPullTempData()
    {
        var (connectionString, factory, api, _) = await ArrangePulledDataAsync(
            nameof(PullComplete_CleansUpPullTempData));
        using var _f = factory;

        var (pullSessionId, _, sessionIds) = await RunPullAsync(api);

        await api.CompletePullAsync(pullSessionId, sessionIds);

        // Shared pull table: no rows may remain for this pull session after completion.
        // Temp tables key the staging rows by the pull session id in a column named SessionId.
        using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var remaining = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TempPullCustomers WHERE SessionId = @Id",
            new { Id = pullSessionId.ToString() });

        remaining.Should().Be(0,
            "pull/complete must clean up the temp pull data it staged");
    }

    // -- helpers --------------------------------------------------------------

    /// <summary>Pushes 3 customers from a different device, then returns a client API for the puller.</summary>
    private async Task<(string ConnectionString, SyncWebApplicationFactory Factory, HttpSyncServerApi Api, Guid PullingDevice)>
        ArrangePulledDataAsync(string testName)
    {
        var connectionString = await _dbFactory.CreateDatabaseAsync(testName);

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var metadataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, metadataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        // A *different* device pushes, so the pulling device genuinely has unseen records.
        var pushingDevice = Guid.NewGuid();
        var begin = new PushSessionBeginRequest
        {
            DeviceId = pushingDevice,
            Tables = TestDatabaseFactory.GetTableSyncInfos(config, new Dictionary<string, int> { ["Customers"] = 3 })
        };
        var pushSessionId = (await sessionTracker.CreatePushSessionAsync(begin)).SessionId;

        await tempTableManager.InsertBatchAsync(pushSessionId, "Customers", TestDataGenerator.CreateCustomersDict(3, "vet-user"));
        await sessionTracker.CompleteTableAsync(pushSessionId, "Customers", totalRecordsSent: 3);
        await sessionTracker.MarkSessionReadyAsync(pushSessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        var pullingDevice = Guid.NewGuid();
        var factory = new SyncWebApplicationFactory(connectionString);
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-SyncSystem-Protocol", "1");

        var api = new HttpSyncServerApi(httpClient, "/api", pullingDevice);
        return (connectionString, factory, api, pullingDevice);
    }

    /// <summary>Runs pull begin + batch (no complete) and returns the session id, rows pulled and session ids seen.</summary>
    private static async Task<(Guid PullSessionId, int Pulled, List<Guid> SessionIds)> RunPullAsync(HttpSyncServerApi api)
    {
        var response = await api.BeginPullAsync(new List<string> { "Customers" });

        var sessionIds = new List<Guid>();
        var pulled = 0;
        var offset = 0;

        while (true)
        {
            var (records, hasMore, _) = await api.PullBatchAsync<Customer>(response.PullSessionId, offset, 100);
            var list = records.ToList();
            if (list.Count == 0)
                break;

            foreach (var r in list.Where(r => r.SyncSessionId != null))
            {
                if (!sessionIds.Contains(r.SyncSessionId!.Value))
                    sessionIds.Add(r.SyncSessionId.Value);
            }

            pulled += list.Count;
            offset += list.Count;

            if (!hasMore)
                break;
        }

        return (response.PullSessionId, pulled, sessionIds);
    }
}
