using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Models;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Server.Database;
using SyncSession.Server.Models;
using SyncSession.Server.Services;
using MySqlConnector;
using Xunit;
using SyncSession.Samples.Shared.TestData;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// DTO for querying customer records in tests
/// </summary>
public class CustomerQueryResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public string ModifiedByUserId { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public Guid? SyncSessionId { get; set; }
    // NOTE: SyncVersion is NOT on records - only on SessionRecords table
}

/// <summary>
/// Session 16: End-to-end integration tests with real MariaDB container
/// Tests complete push/pull flow with background queue processing
/// </summary>
[Collection("MariaDB Collection")]
public class EndToEndIntegrationTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private readonly ServerSyncConfiguration _config;
    private string _testConnectionString = string.Empty;

    public EndToEndIntegrationTests(MariaDbFixture fixture)
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
        _testConnectionString = await _fixture.CreateTestDatabaseAsync(nameof(EndToEndIntegrationTests));
    }

    public async Task DisposeAsync()
    {
        // No cleanup needed - MariaDbFixture handles database lifecycle
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CompletePushFlow_WithBackgroundProcessing_Success()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);


        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);
        
        var clientId = Guid.NewGuid();
        var customerId = Guid.NewGuid().ToString();

        // Step 1: Begin push session
        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = new List<TableSyncInfo>
            {
                new TableSyncInfo
                {
                    TableName = "Customers",
                    EstimatedRecordCount = 1
                }
            }
        };

        var beginResponse = await sessionTracker.CreatePushSessionAsync(beginRequest);

        // Assert: Session created
        beginResponse.Success.Should().BeTrue();
        beginResponse.SessionId.Should().NotBeEmpty();
        beginResponse.Tables.Should().ContainKey("Customers");

        var sessionId = beginResponse.SessionId;

        // Step 2: Insert record into temp table using TestDataGenerator
        var customers = TestDataGenerator.CreateCustomersDict(1, "user-123");
        customers[0]["Id"] = customerId; // Use specific ID for later verification
        customers[0]["Name"] = "John Doe";
        customers[0]["Email"] = "john@example.com";
        customers[0]["Phone"] = "555-1234";
        customers[0]["Address"] = "123 Main St";
        
        await tempTableManager.InsertBatchAsync(sessionId, "Customers", customers);

        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();

        // Step 3: Complete table
        var tableCompleteResponse = await sessionTracker.CompleteTableAsync(sessionId, "Customers", totalRecordsSent: 1);
        
        tableCompleteResponse.Success.Should().BeTrue();
        tableCompleteResponse.ActualRecordCount.Should().Be(1);
        tableCompleteResponse.CountMatches.Should().BeTrue();

        // Step 4: Verify SyncSessionTables has correct ActualRecordCount (before processing deletes rows)
        var sessionTables = await serverDb.GetSessionTablesAsync(sessionId);
        sessionTables.Should().HaveCount(1);
        sessionTables[0].TableName.Should().Be("Customers");
        sessionTables[0].ActualRecordCount.Should().Be(1,
            "GetSessionTablesAsync should reflect the count recorded by CompleteTableAsync");

        // Step 5: Mark session ready
        var readyResult = await sessionTracker.MarkSessionReadyAsync(sessionId);
        readyResult.Should().BeTrue();

        // Step 6: Background queue processes the session
        var processedCount = await queueProcessor.ProcessReadySessionsAsync(default);
        processedCount.Should().Be(1);

        // Step 7: Verify session committed
        var session = await serverDb.GetSessionAsync(sessionId);
        session.Should().NotBeNull();
        session!.Status.Should().Be("Committed");
        session.SyncVersion.Should().BeGreaterThan(0);

        // Step 8: Verify customer in main table
        var customer = await connection.QuerySingleOrDefaultAsync<CustomerQueryResult>(
            "SELECT * FROM Customers WHERE Id = @Id",
            new { Id = customerId });

        customer.Should().NotBeNull();
        customer!.Name.Should().Be("John Doe");
        customer.Email.Should().Be("john@example.com");
        customer.Phone.Should().Be("555-1234");
        customer.Address.Should().Be("123 Main St");
        customer.ModifiedByUserId.Should().Be("user-123");
        customer.SyncSessionId.Should().Be(sessionId);
        customer.IsDeleted.Should().BeFalse();

        // Step 9: Verify temp table cleaned up
        var tempCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TempPushCustomers WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });
        
        tempCount.Should().Be(0, "temp table should be cleaned up after processing");
    }

    [Fact]
    public async Task MultiClient_ConcurrentPush_BothClientsSucceed()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var client1Id = Guid.NewGuid();
        var client2Id = Guid.NewGuid();
        var customer1Id = Guid.NewGuid().ToString();
        var customer2Id = Guid.NewGuid().ToString();

        // Client 1: Push customer
        var session1Request = new PushSessionBeginRequest
        {
            DeviceId = client1Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var session1Response = await sessionTracker.CreatePushSessionAsync(session1Request);
        var session1Id = session1Response.SessionId;

        // Client 1: Create and insert customer using TestDataGenerator
        var customer1 = TestDataGenerator.CreateCustomersDict(1, "user-alice");
        customer1[0]["Id"] = customer1Id;
        customer1[0]["Name"] = "Alice";
        customer1[0]["Email"] = "alice@example.com";
        await tempTableManager.InsertBatchAsync(session1Id, "Customers", customer1);

        await sessionTracker.CompleteTableAsync(session1Id, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(session1Id);

        // Client 2: Push customer (concurrent)
        var session2Request = new PushSessionBeginRequest
        {
            DeviceId = client2Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var session2Response = await sessionTracker.CreatePushSessionAsync(session2Request);
        var session2Id = session2Response.SessionId;

        // Client 2: Create and insert customer using TestDataGenerator
        var customer2 = TestDataGenerator.CreateCustomersDict(1, "user-bob");
        customer2[0]["Id"] = customer2Id;
        customer2[0]["Name"] = "Bob";
        customer2[0]["Email"] = "bob@example.com";
        await tempTableManager.InsertBatchAsync(session2Id, "Customers", customer2);

        await sessionTracker.CompleteTableAsync(session2Id, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(session2Id);

        // Process both sessions
        var processedCount = await queueProcessor.ProcessReadySessionsAsync(default);
        processedCount.Should().Be(2);

        // Verify both sessions committed
        var session1 = await serverDb.GetSessionAsync(session1Id);
        var session2 = await serverDb.GetSessionAsync(session2Id);

        session1!.Status.Should().Be("Committed");
        session2!.Status.Should().Be("Committed");
        session1.SyncVersion.Should().BeGreaterThan(0);
        session2.SyncVersion.Should().BeGreaterThan(session1.SyncVersion!.Value, "sessions should have sequential versions");

        // Verify both customers exist
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();
        var customers = await connection.QueryAsync<CustomerQueryResult>("SELECT * FROM Customers ORDER BY Name");
        var customerList = customers.ToList();
        customerList.Should().HaveCount(2);
        
        var alice = customerList.First(c => c.Name == "Alice");
        var bob = customerList.First(c => c.Name == "Bob");
        
        alice.ModifiedByUserId.Should().Be("user-alice");
        bob.ModifiedByUserId.Should().Be("user-bob");
    }

    [Fact]
    public async Task SessionBasedTracking_PreventsLostRecords()
    {
        // This test validates the core innovation: session-based tracking prevents lost records
        
        // Arrange

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var client1Id = Guid.NewGuid();
        var client2Id = Guid.NewGuid();
        var device2Id = Guid.NewGuid(); // Device for client2

        // Client 1 pushes a record
        var session1Request = new PushSessionBeginRequest
        {
            DeviceId = client1Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var session1Response = await sessionTracker.CreatePushSessionAsync(session1Request);
        var session1Id = session1Response.SessionId;

        // Client 1: Create and insert customer using TestDataGenerator
        var customer1 = TestDataGenerator.CreateCustomersDict(1, "user-1");
        customer1[0]["Name"] = "Record1";
        customer1[0]["Email"] = "record1@example.com";
        await tempTableManager.InsertBatchAsync(session1Id, "Customers", customer1);

        await sessionTracker.CompleteTableAsync(session1Id, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(session1Id);

        // Process session 1
        await queueProcessor.ProcessReadySessionsAsync(default);

        var session1 = await serverDb.GetSessionAsync(session1Id);
        session1!.Status.Should().Be("Committed");
        var version1 = session1.SyncVersion!.Value;

        // Get unseen sessions for client 2 BEFORE marking any as processed
        var unseenBefore = await serverDb.FindUnseenSessionIdsAsync(device2Id);
        unseenBefore.Should().Contain(session1Id, "client2 hasn't processed session1 yet");

        // Client 2 would now pull and mark session1 as processed
        await serverDb.MarkSessionsProcessedAsync(device2Id, new[] { session1Id });

        // Client 1 pushes another record (while client2 is syncing)
        var session2Request = new PushSessionBeginRequest
        {
            DeviceId = client1Id,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var session2Response = await sessionTracker.CreatePushSessionAsync(session2Request);
        var session2Id = session2Response.SessionId;

        // Client 1: Create and insert second customer using TestDataGenerator
        var customer2 = TestDataGenerator.CreateCustomersDict(1, "user-1");
        customer2[0]["Name"] = "Record2";
        customer2[0]["Email"] = "record2@example.com";
        await tempTableManager.InsertBatchAsync(session2Id, "Customers", customer2);

        await sessionTracker.CompleteTableAsync(session2Id, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(session2Id);
        await queueProcessor.ProcessReadySessionsAsync(default);

        var session2 = await serverDb.GetSessionAsync(session2Id);
        session2!.Status.Should().Be("Committed");

        // CRITICAL TEST: Client2 pulls again - should get session2 even though they're "at" version1's level
        var unseenAfter = await serverDb.FindUnseenSessionIdsAsync(device2Id);
        unseenAfter.Should().Contain(session2Id, "session-based tracking ensures no lost records");
        unseenAfter.Should().NotContain(session1Id, "session1 was already processed");

        // Verify both records exist
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();
        var customers = await connection.QueryAsync<CustomerQueryResult>("SELECT * FROM Customers ORDER BY Name");
        var customerList = customers.ToList();
        customerList.Should().HaveCount(2);
        customerList.Select(c => c.Name).Should().Contain(new[] { "Record1", "Record2" });
    }

    #region ModifiedAtUtc Ownership (22h)

    [Fact]
    public async Task Push_PreservesClientModifiedAtUtc_AfterCommit()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var clientId = Guid.NewGuid();
        var customerId = Guid.NewGuid().ToString();

        // Truncate to seconds to avoid sub-millisecond rounding differences with MySQL
        var clientTimestamp = DateTime.UtcNow.AddHours(-1);
        clientTimestamp = new DateTime(clientTimestamp.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var beginResponse = await sessionTracker.CreatePushSessionAsync(beginRequest);
        var sessionId = beginResponse.SessionId;

        // Set explicit client timestamp
        var customers = TestDataGenerator.CreateCustomersDict(1, "user-123");
        customers[0]["Id"] = customerId;
        customers[0]["ModifiedAtUtc"] = clientTimestamp;

        await tempTableManager.InsertBatchAsync(sessionId, "Customers", customers);
        await sessionTracker.CompleteTableAsync(sessionId, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(sessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Assert
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();
        var customer = await connection.QuerySingleOrDefaultAsync<CustomerQueryResult>(
            "SELECT * FROM Customers WHERE Id = @Id", new { Id = customerId });

        customer.Should().NotBeNull();
        customer!.ModifiedAtUtc.Should().BeCloseTo(clientTimestamp, TimeSpan.FromSeconds(1),
            "server should preserve the client-provided ModifiedAtUtc, not overwrite it");
    }

    [Fact]
    public async Task Push_NullModifiedAtUtc_FallsBackToServerTime()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var clientId = Guid.NewGuid();
        var customerId = Guid.NewGuid().ToString();
        var beforePush = DateTime.UtcNow;

        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
        };
        var beginResponse = await sessionTracker.CreatePushSessionAsync(beginRequest);
        var sessionId = beginResponse.SessionId;

        // Explicitly null out ModifiedAtUtc (CreateCustomersDict sets it to UtcNow by default)
        var customers = TestDataGenerator.CreateCustomersDict(1, "user-123");
        customers[0]["Id"] = customerId;
        customers[0]["ModifiedAtUtc"] = null;

        await tempTableManager.InsertBatchAsync(sessionId, "Customers", customers);
        await sessionTracker.CompleteTableAsync(sessionId, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(sessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Assert
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();
        var customer = await connection.QuerySingleOrDefaultAsync<CustomerQueryResult>(
            "SELECT * FROM Customers WHERE Id = @Id", new { Id = customerId });

        customer.Should().NotBeNull();
        customer!.ModifiedAtUtc.Should().NotBe(default(DateTime),
            "server should fall back to UTC_TIMESTAMP(6) when client does not provide ModifiedAtUtc");
        customer.ModifiedAtUtc.Should().BeOnOrAfter(beforePush.AddSeconds(-1),
            "server-generated timestamp should be close to push time");
    }

    #endregion

    [Fact]
    public async Task LargeDataset_UsesSharedTables_ProcessesSuccessfully()
    {
        // Arrange

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var clientId = Guid.NewGuid();
        var recordCount = 100; // Small test, but validates the flow

        // Begin push session
        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = new List<TableSyncInfo> { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = recordCount } }
        };
        var beginResponse = await sessionTracker.CreatePushSessionAsync(beginRequest);
        var sessionId = beginResponse.SessionId;

        // Should use shared table for small dataset
        beginResponse.Tables["Customers"].UsesSharedTable.Should().BeTrue();

        // Insert multiple records using TestDataGenerator
        var customers = TestDataGenerator.CreateCustomersDict(recordCount, "batch-user");
        for (int i = 0; i < recordCount; i++)
        {
            customers[i]["Name"] = $"Customer {i}";
            customers[i]["Email"] = $"customer{i}@example.com";
        }
        await tempTableManager.InsertBatchAsync(sessionId, "Customers", customers);

        // Complete and process
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();
        await sessionTracker.CompleteTableAsync(sessionId, "Customers", recordCount);
        await sessionTracker.MarkSessionReadyAsync(sessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Verify all records processed
        var actualCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Customers");
        actualCount.Should().Be(recordCount);

        // Verify session committed
        var session = await serverDb.GetSessionAsync(sessionId);
        session!.Status.Should().Be("Committed");
    }

    [Fact]
    public async Task GetSessionTablesAsync_AfterCompleteTable_ReturnsCorrectActualRecordCount()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        var clientId = Guid.NewGuid();
        const int recordCount = 5;

        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = new List<TableSyncInfo> { new() { TableName = "Customers", EstimatedRecordCount = recordCount } }
        };
        var beginResponse = await sessionTracker.CreatePushSessionAsync(beginRequest);
        var sessionId = beginResponse.SessionId;

        var customers = TestDataGenerator.CreateCustomersDict(recordCount, "user-test");
        await tempTableManager.InsertBatchAsync(sessionId, "Customers", customers);
        await sessionTracker.CompleteTableAsync(sessionId, "Customers", recordCount);

        // Act — check before processing (rows exist until queue processor deletes them)
        var sessionTables = await serverDb.GetSessionTablesAsync(sessionId);

        // Assert
        sessionTables.Should().HaveCount(1);
        var customersTable = sessionTables[0];
        customersTable.TableName.Should().Be("Customers");
        customersTable.ActualRecordCount.Should().Be(recordCount,
            "server should record the exact count of records received per table");
    }

    [Fact]
    public async Task CompleteTable_CountMismatch_ReturnsCountMismatch()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        var clientId = Guid.NewGuid();
        const int actualRecordsInserted = 5;
        const int countClientClaims = 6; // deliberate mismatch

        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = new List<TableSyncInfo> { new() { TableName = "Customers", EstimatedRecordCount = actualRecordsInserted } }
        };
        var beginResponse = await sessionTracker.CreatePushSessionAsync(beginRequest);
        var sessionId = beginResponse.SessionId;

        var customers = TestDataGenerator.CreateCustomersDict(actualRecordsInserted, "user-test");
        await tempTableManager.InsertBatchAsync(sessionId, "Customers", customers);

        // Act — report 6 sent but server only has 5
        var response = await sessionTracker.CompleteTableAsync(sessionId, "Customers", countClientClaims);

        // Assert
        response.Success.Should().BeTrue("CompleteTableAsync itself succeeds — it just reports the mismatch");
        response.ActualRecordCount.Should().Be(actualRecordsInserted);
        response.CountMatches.Should().BeFalse("client claimed 6 records but server received 5");
    }
}
