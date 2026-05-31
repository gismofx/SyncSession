using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Core.DTOs;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Server.Database;
using SyncSession.Server.Models;
using SyncSession.Server.Services;
using MySqlConnector;
using Xunit;
using SyncSession.Samples.Shared.TestData;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// Session 16f: Pull integration tests
/// Validates complete pull flow: begin → batch → complete
/// Tests session-based tracking prevents lost records on pull side
/// </summary>
[Collection("MariaDB Collection")]
public class PullIntegrationTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private readonly ServerSyncConfiguration _config;
    private string _testConnectionString = string.Empty;

    public PullIntegrationTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
        _config = new ServerSyncConfiguration
        {
            PushSharedTableThreshold = 10000,
            PullSharedTableThreshold = 10000
        };
    }

    public async Task InitializeAsync()
    {
        // Create isolated test database
        _testConnectionString = await _fixture.CreateTestDatabaseAsync(nameof(PullIntegrationTests));
    }

    public async Task DisposeAsync()
    {
        // No cleanup needed - MariaDbFixture handles database lifecycle
        await Task.CompletedTask;
    }

    /// <summary>
    /// Test #1: Complete pull flow - single client
    /// Push record from client1, pull from client2, verify data received
    /// </summary>
    [Fact]
    public async Task CompletePullFlow_SingleClient_Success()
    {
        // Arrange

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var client1Id = Guid.NewGuid(); // Will push
        var client2Id = Guid.NewGuid(); // Will pull
        var device2Id = Guid.NewGuid(); // Device for client2
        var customerId = Guid.NewGuid();

        // Step 1: Client 1 pushes a record
        var pushRequest = new PushSessionBeginRequest
        {
            DeviceId = client1Id, // Use same for push in tests
            Tables = new List<TableSyncInfo>
            {
                new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 }
            }
        };
        var pushResponse = await sessionTracker.CreatePushSessionAsync(pushRequest);
        var pushSessionId = pushResponse.SessionId;

        // Create test customer and insert via service layer
        var customer = TestDataGenerator.CreateCustomer(
            id: customerId,
            name: "Alice Smith",
            email: "alice@example.com",
            phone: "555-0001",
            modifiedByUserId: "user-alice"
        );

        await tempTableManager.InsertBatchAsync(
            pushSessionId,
            "Customers",
            new[] { EntityReflectionHelper.EntityToDictionary(customer) }
        );

        await sessionTracker.CompleteTableAsync(pushSessionId, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(pushSessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Verify push committed
        var pushSession = await serverDb.GetSessionAsync(pushSessionId);
        pushSession!.Status.Should().Be("Committed");

        // DEBUG: Check SyncSessionId in Customers table after push
        using (var debugConn = await serverDb.GetConnectionAsync())
        {
            var debugCustomers = await debugConn.QueryAsync<dynamic>(
                "SELECT Id, Name, SyncSessionId FROM Customers LIMIT 5");
            var debugList = debugCustomers.ToList();
            // SET BREAKPOINT HERE - Inspect debugList to see SyncSessionId values
            Console.WriteLine($"DEBUG: Customers table has {debugList.Count} records");
            foreach (var c in debugList)
            {
                Console.WriteLine($"  Id={c.Id}, Name={c.Name}, SyncSessionId={c.SyncSessionId}");
            }
        }

        // Step 2: Client 2 begins pull session
        var pullRequest = new PullSessionBeginRequest
        {
            DeviceId = device2Id,
            TableNames = new[] { "Customers" }
        };
        var pullResponse = await sessionTracker.CreatePullSessionAsync(pullRequest);

        // Assert pull session created
        pullResponse.Success.Should().BeTrue();
        pullResponse.PullSessionId.Should().NotBeEmpty();
        pullResponse.Tables.Should().ContainKey("Customers");
        pullResponse.Tables["Customers"].TotalRecords.Should().Be(1);

        var pullSessionId = pullResponse.PullSessionId;
        var metadata = pullResponse.Tables["Customers"];

        // Step 3: Get batch
        var batch1 = await tempTableManager.GetPullBatchAsync(
            pullSessionId, "Customers", offset: 0, limit: 1000);

        // Assert batch contents
        batch1.Records.Should().HaveCount(1);
        batch1.HasMore.Should().BeFalse();
        batch1.TotalRecords.Should().Be(1);

        var record = batch1.Records.First();
        record["Id"].ToString().Should().Be(customerId.ToString());
        record["Name"].ToString().Should().Be("Alice Smith");
        record["Email"].ToString().Should().Be("alice@example.com");
        record["Phone"].ToString().Should().Be("555-0001");
        record["ModifiedByUserId"].ToString().Should().Be("user-alice");

        // Step 4: Complete pull session
        var completeRequest = new PullSessionCompleteRequest
        {
            PullSessionId = pullSessionId,
            ProcessedSessionIds = new[] { pushSessionId },
            Tables = new Dictionary<string, SyncSessionTableMetadata>
            {
                ["Customers"] = metadata
            }
        };

        await serverDb.MarkSessionsProcessedAsync(device2Id, completeRequest.ProcessedSessionIds);
        await tempTableManager.CleanupPullSessionAsync(pullSessionId, completeRequest.Tables.Values);

        // Step 5: Verify cleanup - temp pull table should be empty
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();
        var tempTableName = metadata.TempTableName;
        var tempCount = await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM `{tempTableName}` WHERE SessionId = @PullSessionId",
            new { PullSessionId = pullSessionId.ToString() });

        tempCount.Should().Be(0, "pull temp table should be cleaned up");

        // Step 6: Verify client marked session as processed
        var unseenSessions = await serverDb.FindUnseenSessionIdsAsync(device2Id);
        unseenSessions.Should().NotContain(pushSessionId, "device2 marked session as processed");
    }

    /// <summary>
    /// Test #2: Multi-client pull after concurrent push
    /// Two clients push concurrently, third client pulls and receives both records
    /// </summary>
    [Fact]
    public async Task MultiClient_PullAfterConcurrentPush_BothReceiveCorrectRecords()
    {
        // Arrange

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance );
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var client1Id = Guid.NewGuid(); // Will push record A
        var client2Id = Guid.NewGuid(); // Will push record B
        var client3Id = Guid.NewGuid(); // Will pull both
        var device3Id = Guid.NewGuid(); // Device for client3
        var customerAId = Guid.NewGuid();
        var customerBId = Guid.NewGuid();

        // Step 1: Client1 pushes record A
        var session1Request = new PushSessionBeginRequest
        {
            DeviceId = client1Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var session1Response = await sessionTracker.CreatePushSessionAsync(session1Request);
        var session1Id = session1Response.SessionId;

        // Create test customer A and insert via service layer
        var customerA = TestDataGenerator.CreateCustomer(
            id: customerAId,
            name: "Alice Anderson",
            email: "alice@example.com",
            modifiedByUserId: "user-alice"
        );

        await tempTableManager.InsertBatchAsync(
            session1Id,
            "Customers",
            new[] { EntityReflectionHelper.EntityToDictionary(customerA) }
        );

        await sessionTracker.CompleteTableAsync(session1Id, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(session1Id);

        // Step 2: Client2 pushes record B (concurrent - before session1 processes)
        var session2Request = new PushSessionBeginRequest
        {
            DeviceId = client2Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var session2Response = await sessionTracker.CreatePushSessionAsync(session2Request);
        var session2Id = session2Response.SessionId;

        // Create test customer B and insert via service layer
        var customerB = TestDataGenerator.CreateCustomer(
            id: customerBId,
            name: "Bob Brown",
            email: "bob@example.com",
            modifiedByUserId: "user-bob"
        );

        await tempTableManager.InsertBatchAsync(
            session2Id,
            "Customers",
            new[] { EntityReflectionHelper.EntityToDictionary(customerB) }
        );

        await sessionTracker.CompleteTableAsync(session2Id, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(session2Id);

        // Step 3: Process both sessions
        var processedCount = await queueProcessor.ProcessReadySessionsAsync(default);
        processedCount.Should().Be(2);

        // Verify both committed
        var session1 = await serverDb.GetSessionAsync(session1Id);
        var session2 = await serverDb.GetSessionAsync(session2Id);
        session1!.Status.Should().Be("Committed");
        session2!.Status.Should().Be("Committed");

        // Step 4: Client3 begins pull session
        var pullRequest = new PullSessionBeginRequest
        {
            DeviceId = device3Id,
            TableNames = new[] { "Customers" }
        };
        var pullResponse = await sessionTracker.CreatePullSessionAsync(pullRequest);

        // Assert pull session has both records
        pullResponse.Success.Should().BeTrue();
        pullResponse.Tables["Customers"].TotalRecords.Should().Be(2, "both records should be available for pull");

        var pullSessionId = pullResponse.PullSessionId;
        var metadata = pullResponse.Tables["Customers"];

        // Step 5: Get batch
        var batch2 = await tempTableManager.GetPullBatchAsync(
            pullSessionId, "Customers", offset: 0, limit: 1000);

        // Assert both records present
        batch2.Records.Should().HaveCount(2);
        batch2.HasMore.Should().BeFalse();
        batch2.TotalRecords.Should().Be(2);

        var recordA = batch2.Records.FirstOrDefault(r => Guid.Parse(r["Id"].ToString()) == customerAId);
        var recordB = batch2.Records.FirstOrDefault(r => Guid.Parse(r["Id"].ToString()) == customerBId);

        recordA.Should().NotBeNull("record A should be in pull batch");
        recordB.Should().NotBeNull("record B should be in pull batch");

        recordA!["Name"].ToString().Should().Be("Alice Anderson");
        recordA["ModifiedByUserId"].ToString().Should().Be("user-alice");

        recordB!["Name"].ToString().Should().Be("Bob Brown");
        recordB["ModifiedByUserId"].ToString().Should().Be("user-bob");

        // Step 6: Complete pull session
        await serverDb.MarkSessionsProcessedAsync(device3Id, new[] { session1Id, session2Id });
        await tempTableManager.CleanupPullSessionAsync(pullSessionId, new[] { metadata });

        // Verify cleanup
        var unseenSessions = await serverDb.FindUnseenSessionIdsAsync(device3Id);
        unseenSessions.Should().NotContain(session1Id);
        unseenSessions.Should().NotContain(session2Id);
    }

    /// <summary>
    /// Test #3: Pull batching with large dataset
    /// Push many records, pull using client HTTP API with automatic batching, verify all received
    /// </summary>
    [Fact]
    public async Task PullBatching_LargeDataset_PaginatesCorrectly()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance  );

        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var client1Id = Guid.NewGuid(); // Will push many records
        var client2Id = Guid.NewGuid(); // Will pull with batching
        var device2Id = Guid.NewGuid(); // Device for client2
        const int totalRecords = 2500;

        // Step 1: Client1 pushes large dataset
        var pushRequest = new PushSessionBeginRequest
        {
            DeviceId = client1Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = totalRecords } }
        };
        var pushResponse = await sessionTracker.CreatePushSessionAsync(pushRequest);
        var pushSessionId = pushResponse.SessionId;

        // Generate test customers
        var customers = TestDataGenerator.CreateCustomers(totalRecords, "user-batch", isDirty: false);
        var customerDicts = customers.Select(c => EntityReflectionHelper.EntityToDictionary(c)).ToList();

        // Insert in batches (simulate client batching)
        for (int i = 0; i < totalRecords; i += 1000)
        {
            var batch = customerDicts.Skip(i).Take(1000).ToList();
            await tempTableManager.InsertBatchAsync(pushSessionId, "Customers", batch);
        }

        await sessionTracker.CompleteTableAsync(pushSessionId, "Customers", totalRecords);
        await sessionTracker.MarkSessionReadyAsync(pushSessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Verify push committed
        var pushSession = await serverDb.GetSessionAsync(pushSessionId);
        pushSession!.Status.Should().Be("Committed");

        // Step 2: Client2 begins pull session using HTTP client
        //var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost") }; // Not used, just for constructor
        //var serverClient = new SyncSystem.Client.Http.HttpServerClient(httpClient);
        
        // Begin pull via session tracker (simulates HTTP endpoint)
        var pullRequest = new PullSessionBeginRequest
        {
            DeviceId = device2Id,
            TableNames = new[] { "Customers" }
        };
        var pullResponse = await sessionTracker.CreatePullSessionAsync(pullRequest);

        pullResponse.Success.Should().BeTrue();
        pullResponse.Tables["Customers"].TotalRecords.Should().Be(totalRecords);

        var pullSessionId = pullResponse.PullSessionId;
        
        // Step 3: Pull all records using direct batching API (simulates HTTP client batching)
        // This tests the actual batching logic that HttpServerClient.PullRecordsAsync uses
        var allRecords = new List<Dictionary<string, object?>>();
        var offset = 0;
        const int batchSize = 1000;
        var expectedBatches = (int)Math.Ceiling((double)totalRecords / batchSize);
        var batchCount = 0;

        while (true)
        {
            var batchResult = await tempTableManager.GetPullBatchAsync(
                pullSessionId, "Customers", offset, batchSize);

            allRecords.AddRange(batchResult.Records);
            batchCount++;

            // Verify batch size (last batch may be smaller)
            if (batchResult.HasMore)
            {
                batchResult.Records.Should().HaveCount(batchSize, $"batch {batchCount} should be full");
            }
            else
            {
                // Last batch
                var expectedLastBatchSize = totalRecords - offset;
                batchResult.Records.Should().HaveCount(expectedLastBatchSize, "last batch size correct");
                batchResult.TotalRecords.Should().Be(totalRecords);
                break;
            }

            offset += batchSize;
        }

        // Step 4: Verify all records received
        allRecords.Should().HaveCount(totalRecords, "all records retrieved across batches");
        batchCount.Should().Be(expectedBatches, $"expected {expectedBatches} batches");

        // Verify unique IDs (no duplicates)
        var uniqueIds = allRecords.Select(r => r["Id"].ToString()).Distinct().Count();
        uniqueIds.Should().Be(totalRecords, "all records are unique");

        // Step 5: Complete pull session
        await serverDb.MarkSessionsProcessedAsync(device2Id, new[] { pushSessionId });
        await tempTableManager.CleanupPullSessionAsync(pullSessionId, new[] { pullResponse.Tables["Customers"] });

        // Verify cleanup
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();
        var tempTableName = pullResponse.Tables["Customers"].TempTableName;
        var tempCount = await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM `{tempTableName}` WHERE SessionId = @PullSessionId",
            new { PullSessionId = pullSessionId.ToString() });

        tempCount.Should().Be(0, "pull temp table cleaned up");
    }

    /// <summary>
    /// Test #4: Session-based pull prevents lost records (CRITICAL TEST)
    /// Validates core innovation on pull side: client doesn't lose records during concurrent operations
    /// 
    /// Scenario that would fail with version-based tracking:
    /// 1. ClientA pushes records → session1 created
    /// 2. ClientB begins pull (sees session1)
    /// 3. Session1 commits with version 101
    /// 4. ClientC pushes records → session2 created
    /// 5. Session2 commits with version 102
    /// 6. ClientB completes pull, marks session1 processed
    /// 7. ClientB pulls again → MUST receive session2 records
    /// 
    /// With version-based: ClientB would think "I'm at version 101" and miss session2
    /// With session-based: ClientB knows "I processed session1, not session2" and gets session2
    /// </summary>
    [Fact]
    public async Task SessionBasedPull_PreventsLostRecords()
    {
        // Arrange

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var clientAId = Guid.NewGuid(); // Will push first batch
        var clientBId = Guid.NewGuid(); // Will pull during concurrent operations
        var deviceBId = Guid.NewGuid(); // Device for clientB
        var clientCId = Guid.NewGuid(); // Will push second batch
        var customerAId = Guid.NewGuid();
        var customerCId = Guid.NewGuid();

        // Step 1: ClientA pushes record → creates session1
        var session1Request = new PushSessionBeginRequest
        {
            DeviceId = clientAId,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var session1Response = await sessionTracker.CreatePushSessionAsync(session1Request);
        var session1Id = session1Response.SessionId;

        var customerA = TestDataGenerator.CreateCustomer(
            id: customerAId,
            name: "Alice from ClientA",
            email: "alice@clienta.com",
            modifiedByUserId: "user-clientA"
        );
        await tempTableManager.InsertBatchAsync(
            session1Id,
            "Customers",
            new[] { EntityReflectionHelper.EntityToDictionary(customerA) }
        );

        await sessionTracker.CompleteTableAsync(session1Id, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(session1Id);

        // Step 2: ClientB begins pull BEFORE session1 commits
        var pull1Request = new PullSessionBeginRequest
        {
            DeviceId = deviceBId,
            TableNames = new[] { "Customers" }
        };
        var pull1Response = await sessionTracker.CreatePullSessionAsync(pull1Request);
        var pull1SessionId = pull1Response.PullSessionId;

        // Pull session sees no committed sessions yet (empty response)
        pull1Response.Success.Should().BeTrue();
        pull1Response.Tables.Should().BeEmpty("no committed sessions yet, so no tables to pull");

        // Step 3: Process session1 (commits with version)
        await queueProcessor.ProcessReadySessionsAsync(default);
        var session1 = await serverDb.GetSessionAsync(session1Id);
        session1!.Status.Should().Be("Committed");
        var version1 = session1.SyncVersion!.Value;

        // Step 4: ClientC pushes record → creates session2 (concurrent with ClientB's pull)
        var session2Request = new PushSessionBeginRequest
        {
            DeviceId = clientCId,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var session2Response = await sessionTracker.CreatePushSessionAsync(session2Request);
        var session2Id = session2Response.SessionId;

        var customerC = TestDataGenerator.CreateCustomer(
            id: customerCId,
            name: "Charlie from ClientC",
            email: "charlie@clientc.com",
            modifiedByUserId: "user-clientC"
        );
        await tempTableManager.InsertBatchAsync(
            session2Id,
            "Customers",
            new[] { EntityReflectionHelper.EntityToDictionary(customerC) }
        );

        await sessionTracker.CompleteTableAsync(session2Id, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(session2Id);

        // Step 5: Process session2 (commits with higher version)
        await queueProcessor.ProcessReadySessionsAsync(default);
        var session2 = await serverDb.GetSessionAsync(session2Id);
        session2!.Status.Should().Be("Committed");
        var version2 = session2.SyncVersion!.Value;
        version2.Should().BeGreaterThan(version1, "session2 has higher version");

        // Step 6: ClientB completes FIRST pull (had 0 records, marks no sessions)
        // In version-based sync, client might record "highest version seen = 0"
        // Session-based: ClientB hasn't marked any sessions processed yet
        if (pull1Response.Tables.Any())
        {
            await tempTableManager.CleanupPullSessionAsync(pull1SessionId, pull1Response.Tables.Values);
        }
        // If no tables in response, nothing to cleanup

        // Verify ClientB should see both sessions as unseen
        var unseenBeforePull2 = await serverDb.FindUnseenSessionIdsAsync(deviceBId);
        unseenBeforePull2.Should().Contain(session1Id, "session1 should be unseen");
        unseenBeforePull2.Should().Contain(session2Id, "session2 should be unseen");
        unseenBeforePull2.Should().HaveCount(2, "exactly 2 unseen sessions");

        // Step 7: ClientB pulls AGAIN → session-based tracking ensures BOTH sessions returned
        var pull2Request = new PullSessionBeginRequest
        {
            DeviceId = deviceBId,
            TableNames = new[] { "Customers" }
        };
        var pull2Response = await sessionTracker.CreatePullSessionAsync(pull2Request);

        // Debug: Check what we got back
        pull2Response.Success.Should().BeTrue();
        pull2Response.Tables.Should().ContainKey("Customers", "Customers table should be in response");
        
        // CRITICAL ASSERTION: ClientB should see 2 records (both session1 and session2)
        // Because ClientB hasn't marked ANY sessions as processed yet
        pull2Response.Tables["Customers"].TotalRecords.Should().Be(2, 
            "ClientB should see BOTH session1 and session2 records because it hasn't processed any sessions yet");

        var pull2SessionId = pull2Response.PullSessionId;

        // Get the records
        var pull2Batch = await tempTableManager.GetPullBatchAsync(
            pull2SessionId, "Customers", offset: 0, limit: 1000);

        pull2Batch.Records.Should().HaveCount(2, "both records present");
        
        var recordA = pull2Batch.Records.FirstOrDefault(r => Guid.Parse(r["Id"].ToString()) == customerAId);
        var recordC = pull2Batch.Records.FirstOrDefault(r => Guid.Parse(r["Id"].ToString()) == customerCId);

        recordA.Should().NotBeNull("Alice from session1 present");
        recordC.Should().NotBeNull("Charlie from session2 present");

        recordA!["Name"].ToString().Should().Be("Alice from ClientA");
        recordC!["Name"].ToString().Should().Be("Charlie from ClientC");

        // Complete second pull
        await serverDb.MarkSessionsProcessedAsync(deviceBId, new[] { session1Id, session2Id });
        await tempTableManager.CleanupPullSessionAsync(pull2SessionId, new[] { pull2Response.Tables["Customers"] });

        // Step 8: Verify ClientB now has all sessions marked
        var unseenSessions = await serverDb.FindUnseenSessionIdsAsync(deviceBId);
        unseenSessions.Should().BeEmpty("ClientB marked all sessions processed");
    }

    /// <summary>
    /// Test #5: Pull with no changes returns empty
    /// Client has no unseen sessions, pull returns empty result
    /// </summary>
    [Fact]
    public async Task PullWithNoChanges_ReturnsEmpty()
    {
        // Arrange

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var client1Id = Guid.NewGuid(); // Will push
        var client2Id = Guid.NewGuid(); // Will pull twice
        var device2Id = Guid.NewGuid(); // Device for client2

        // Step 1: Client1 pushes record
        var pushRequest = new PushSessionBeginRequest
        {
            DeviceId = client1Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var pushResponse = await sessionTracker.CreatePushSessionAsync(pushRequest);
        var pushSessionId = pushResponse.SessionId;

        var customer = TestDataGenerator.CreateCustomer(modifiedByUserId: "user-test");
        await tempTableManager.InsertBatchAsync(
            pushSessionId,
            "Customers",
            new[] { EntityReflectionHelper.EntityToDictionary(customer) }
        );

        await sessionTracker.CompleteTableAsync(pushSessionId, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(pushSessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Step 2: Client2 pulls and receives record
        var pull1Request = new PullSessionBeginRequest
        {
            DeviceId = device2Id,
            TableNames = new[] { "Customers" }
        };
        var pull1Response = await sessionTracker.CreatePullSessionAsync(pull1Request);

        pull1Response.Success.Should().BeTrue();
        pull1Response.Tables.Should().ContainKey("Customers");
        pull1Response.Tables["Customers"].TotalRecords.Should().Be(1);

        var pull1SessionId = pull1Response.PullSessionId;

        // Get and verify record
        var pull1Batch = await tempTableManager.GetPullBatchAsync(
            pull1SessionId, "Customers", offset: 0, limit: 1000);
        pull1Batch.Records.Should().HaveCount(1);

        // Complete pull - mark session processed
        await serverDb.MarkSessionsProcessedAsync(device2Id, new[] { pushSessionId });
        await tempTableManager.CleanupPullSessionAsync(pull1SessionId, pull1Response.Tables.Values);

        // Step 3: Client2 pulls again - should get EMPTY response (no new sessions)
        var pull2Request = new PullSessionBeginRequest
        {
            DeviceId = device2Id,
            TableNames = new[] { "Customers" }
        };
        var pull2Response = await sessionTracker.CreatePullSessionAsync(pull2Request);

        // Assert empty response
        pull2Response.Success.Should().BeTrue();
        pull2Response.Tables.Should().BeEmpty("no unseen sessions, nothing to pull");
        pull2Response.PullSessionId.Should().NotBeEmpty("pull session ID still generated");

        // Verify client has processed all sessions
        var unseenSessions = await serverDb.FindUnseenSessionIdsAsync(device2Id);
        unseenSessions.Should().BeEmpty("client marked all sessions processed");
    }

    /// <summary>
    /// Test #6: CRITICAL - SyncSessionId must be present in pulled records
    /// Verifies that SyncSessionId is included in records returned by pull operations.
    /// Without this, clients cannot extract session IDs for tracking, breaking session-based sync.
    /// 
    /// This test exists because GetColumnsForServerSelect previously excluded SyncSessionId
    /// with comment "handled separately" - but it was never actually added to the SELECT.
    /// </summary>
    [Fact]
    public async Task PulledRecords_MustContainSyncSessionId()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var client1Id = Guid.NewGuid(); // Will push
        var client2Id = Guid.NewGuid(); // Will pull
        var device2Id = Guid.NewGuid(); // Device for client2
        var customerId = Guid.NewGuid();

        // Step 1: Client1 pushes a record
        var pushRequest = new PushSessionBeginRequest
        {
            DeviceId = client1Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var pushResponse = await sessionTracker.CreatePushSessionAsync(pushRequest);
        var pushSessionId = pushResponse.SessionId;

        var customer = TestDataGenerator.CreateCustomer(
            id: customerId,
            name: "Test Customer",
            email: "test@example.com",
            modifiedByUserId: "user-test"
        );

        await tempTableManager.InsertBatchAsync(
            pushSessionId,
            "Customers",
            new[] { EntityReflectionHelper.EntityToDictionary(customer) }
        );

        await sessionTracker.CompleteTableAsync(pushSessionId, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(pushSessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Verify push committed
        var pushSession = await serverDb.GetSessionAsync(pushSessionId);
        pushSession!.Status.Should().Be("Committed");

        // Step 2: Client2 begins pull
        var pullRequest = new PullSessionBeginRequest
        {
            DeviceId = device2Id,
            TableNames = new[] { "Customers" }
        };
        var pullResponse = await sessionTracker.CreatePullSessionAsync(pullRequest);
        pullResponse.Success.Should().BeTrue();

        var pullSessionId = pullResponse.PullSessionId;

        // Step 3: Get batch and verify SyncSessionId is present
        var syncSessionBatch = await tempTableManager.GetPullBatchAsync(
            pullSessionId, "Customers", offset: 0, limit: 1000);

        syncSessionBatch.Records.Should().HaveCount(1);
        var record = syncSessionBatch.Records.First();

        // CRITICAL ASSERTION: SyncSessionId MUST be present in pulled records
        record.Should().ContainKey("SyncSessionId", 
            "SyncSessionId is REQUIRED for session-based tracking - without it, clients cannot mark sessions as processed");

        var recordSessionId = record["SyncSessionId"]?.ToString();
        recordSessionId.Should().NotBeNullOrEmpty("SyncSessionId must have a value");
        
        // Verify it matches the push session ID
        Guid.TryParse(recordSessionId, out var parsedSessionId).Should().BeTrue("SyncSessionId should be a valid GUID");
        parsedSessionId.Should().Be(pushSessionId, "record's SyncSessionId should match the session that created it");

        // Step 4: Verify client can extract session ID for tracking
        // This is how the client identifies which session created this record
        var extractedSessionIds = syncSessionBatch.Records
            .Select(r => Guid.Parse(r["SyncSessionId"]!.ToString()!))
            .Distinct()
            .ToList();

        extractedSessionIds.Should().HaveCount(1, "all records from same session");
        extractedSessionIds.First().Should().Be(pushSessionId, "extracted session ID matches push session");
    }

    /// <summary>
    /// Test theory: Dapper auto-converts columns named *SessionId to System.Guid
    /// This test isolates the GUID parsing issue with CHAR(36) columns
    /// </summary>
    [Fact]
    public async Task DebugGuidParsing_CharColumn_WithDapperDynamic()
    {
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();

        try
        {
            // Create test table with CHAR(36) column named SyncSessionId
            await connection.ExecuteAsync(@"
                CREATE TEMPORARY TABLE TestGuidParsing (
                    Id CHAR(36) PRIMARY KEY,
                    SomeText VARCHAR(100),
                    SyncSessionId CHAR(36) NULL
                )");

            // Insert a valid GUID as string
            var testId = Guid.NewGuid();
            var testSessionId = Guid.NewGuid();
            await connection.ExecuteAsync(@"
                INSERT INTO TestGuidParsing (Id, SomeText, SyncSessionId)
                VALUES (@Id, @Text, @SessionId)",
                new 
                { 
                    Id = testId.ToString(), 
                    Text = "Test", 
                    SessionId = testSessionId.ToString() 
                });

            // Try to read with Dapper QueryAsync (dynamic)
            // This should trigger the same error if Dapper auto-converts SyncSessionId to GUID
            var results = await connection.QueryAsync("SELECT * FROM TestGuidParsing");
            var resultList = results.ToList();

            // If we get here without exception, Dapper handled it fine
            resultList.Should().HaveCount(1);
            var record = (IDictionary<string, object>)resultList[0];
            
            Console.WriteLine($"SUCCESS (Dynamic): Retrieved record without GUID parsing error");
            Console.WriteLine($"  Id type: {record["Id"]?.GetType().Name}");
            Console.WriteLine($"  SyncSessionId type: {record["SyncSessionId"]?.GetType().Name}");
            Console.WriteLine($"  SyncSessionId value: {record["SyncSessionId"]}");

            // Now try with strongly-typed class
            var typedResults = await connection.QueryAsync<TestGuidParsingClass>("SELECT * FROM TestGuidParsing");
            var typedList = typedResults.ToList();

            typedList.Should().HaveCount(1);
            var typedRecord = typedList[0];

            Console.WriteLine($"SUCCESS (Strongly-Typed): Retrieved record with Guid property");
            Console.WriteLine($"  Id: {typedRecord.Id}");
            Console.WriteLine($"  SyncSessionId: {typedRecord.SyncSessionId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    // Test class for strongly-typed Dapper deserialization
    private class TestGuidParsingClass
    {
        public Guid Id { get; set; }
        public string SomeText { get; set; } = string.Empty;
        public Guid? SyncSessionId { get; set; }
    }

    /// <summary>
    /// Test the exact snapshot scenario: Insert into business table, snapshot to temp pull table, read
    /// </summary>
    [Fact]
    public async Task DebugGuidParsing_ActualSnapshotScenario()
    {
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        using var connection = await serverDb.GetConnectionAsync();

        try
        {
            // Step 1: Insert a record into Customers table with SyncSessionId
            var customerId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            
            await connection.ExecuteAsync(@"
                INSERT INTO Customers (Id, TenantId, Name, Email, ModifiedAtUtc, SyncSessionId, ModifiedByUserId, IsDeleted)
                VALUES (@Id, @TenantId, @Name, @Email, UTC_TIMESTAMP(6), @SessionId, @UserId, 0)",
                new
                {
                    Id = customerId.ToString(),
                    TenantId = Guid.NewGuid().ToString(),
                    Name = "Debug Test",
                    Email = "debug@test.com",
                    SessionId = sessionId.ToString(),
                    UserId = "debuguser"
                });

            Console.WriteLine($"Inserted customer with SyncSessionId={sessionId}");

            // Step 2: Snapshot into TempPullCustomers (simulate SnapshotRecordsForPullAsync)
            var pullSessionId = Guid.NewGuid();
            
            await connection.ExecuteAsync(@"
                INSERT INTO TempPullCustomers 
                (SessionId, Id, TenantId, Name, Email, Phone, Address, ModifiedAtUtc, SyncSessionId, ModifiedByUserId, IsDeleted)
                SELECT 
                    @PullSessionId,
                    Id, TenantId, Name, Email, Phone, Address, ModifiedAtUtc, SyncSessionId, ModifiedByUserId, IsDeleted
                FROM Customers
                WHERE SyncSessionId = @SessionId",
                new
                {
                    PullSessionId = pullSessionId.ToString(),
                    SessionId = sessionId.ToString()
                });

            Console.WriteLine($"Snapshotted to TempPullCustomers with SessionId={pullSessionId}");

            // Step 3: Try to read with the EXACT pattern from GetPullBatchAsync
            var batchSql = @"
                SELECT `Id`, `Name`, `Email`, `Phone`, `Address`, `TenantId`, `ModifiedAtUtc`, `SyncSessionId`, `ModifiedByUserId`, `IsDeleted`
                FROM TempPullCustomers
                WHERE SessionId = @PullSessionId
                ORDER BY Id
                LIMIT @Limit OFFSET @Offset";

            var dynamicRecords = await connection.QueryAsync(
                batchSql,
                new
                {
                    PullSessionId = pullSessionId.ToString(),
                    Limit = 1000,
                    Offset = 0
                });

            var resultList = dynamicRecords.ToList();
            resultList.Should().HaveCount(1);

            var record = (IDictionary<string, object>)resultList[0];
            Console.WriteLine($"SUCCESS (Query): Read from TempPullCustomers");
            Console.WriteLine($"  SyncSessionId type: {record["SyncSessionId"]?.GetType().Name}");
            Console.WriteLine($"  SyncSessionId value: {record["SyncSessionId"]}");

            // Step 4: Convert to Dictionary<string, object?> (same as GetPullBatchAsync line ~680)
            var dictRecords = resultList
                .Select(r => ((IDictionary<string, object>)r).ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object?)kvp.Value))
                .ToList();

            Console.WriteLine($"SUCCESS (Dictionary): Converted to Dictionary<string, object?>");
            Console.WriteLine($"  SyncSessionId type: {dictRecords[0]["SyncSessionId"]?.GetType().Name}");
            Console.WriteLine($"  SyncSessionId value: {dictRecords[0]["SyncSessionId"]}");

            // Step 5: Convert to Customer entity (same as client does)
            var customerRecord = dictRecords[0];
            var customer = EntityReflectionHelper.DictionaryToEntity<Customer>(customerRecord);

            Console.WriteLine($"SUCCESS (Entity): Converted to Customer entity");
            Console.WriteLine($"  Customer.Id: {customer.Id}");
            Console.WriteLine($"  Customer.SyncSessionId: {customer.SyncSessionId}");
            Console.WriteLine($"  Customer.Name: {customer.Name}");

            customer.Should().NotBeNull();
            customer.Name.Should().Be("Debug Test");

        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"Stack: {ex.StackTrace}");
            throw;
        }
    }
}
