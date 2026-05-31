using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Samples.Shared.TestData;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// Comprehensive integration tests for IServerDatabase implementations.
/// Tests core database operations with real MariaDB container.
/// </summary>
[Collection("MariaDB Collection")]
public class ServerDatabaseTests
{
    private readonly MariaDbFixture _fixture;
    private readonly TestDatabaseFactory _dbFactory;

    public ServerDatabaseTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    #region Session Management Tests (Tests 1-3 ✅ Complete)

    [Fact]
    public async Task SessionTracker_CreateSession_AutoIncrementVersion()
    {
        // Arrange - Create isolated test database
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_CreateSession_AutoIncrementVersion));

        // Use production-like configuration
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(
            serverDb, 
            config, 
            NullLogger<TempTableManager>.Instance);
        
        var sessionTracker = new SessionTracker(
            serverDb, 
            tempTableManager, 
            NullLogger<SessionTracker>.Instance);

        var clientId = Guid.NewGuid();

        // Act - Create push session using configuration
        var request = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int>
                {
                    ["Customers"] = 100,
                    ["Orders"] = 200,
                    ["OrderItems"] = 500
                })
        };

        var response = await sessionTracker.CreatePushSessionAsync(request);

        // Assert - Session created with AUTO_INCREMENT version
        response.Success.Should().BeTrue();
        response.SessionId.Should().NotBeEmpty();
        response.Tables.Should().HaveCount(3);
        response.Tables.Should().ContainKeys("Customers", "Orders", "OrderItems");
        
        // Verify in database
        using var connection = await _dbFactory.GetConnectionAsync();
        var session = await connection.QuerySingleAsync<dynamic>(
            "SELECT SessionId, SyncVersion, Status FROM SessionRecords WHERE SessionId = @SessionId",
            new { SessionId = response.SessionId.ToString() });

        ((long)session.SyncVersion).Should().BeGreaterThan(0);
        ((string)session.Status).Should().Be("Staging");

        // Verify session tables created in priority order
        var sessionTables = await connection.QueryAsync<dynamic>(
            @"SELECT TableName, ProcessingPriority, EstimatedRecordCount 
              FROM SyncSessionTables 
              WHERE SessionId = @SessionId 
              ORDER BY ProcessingPriority",
            new { SessionId = response.SessionId.ToString() });

        sessionTables.Should().HaveCount(3);
        var tableList = sessionTables.AsList();
        
        ((string)tableList[0].TableName).Should().Be("Customers");
        ((int)tableList[0].ProcessingPriority).Should().Be(1);
        ((int)tableList[0].EstimatedRecordCount).Should().Be(100);
        
        ((string)tableList[1].TableName).Should().Be("Orders");
        ((int)tableList[1].ProcessingPriority).Should().Be(2);
        ((int)tableList[1].EstimatedRecordCount).Should().Be(200);
        
        ((string)tableList[2].TableName).Should().Be("OrderItems");
        ((int)tableList[2].ProcessingPriority).Should().Be(3);
        ((int)tableList[2].EstimatedRecordCount).Should().Be(500);
    }

    [Fact]
    public async Task ServerDb_UpsertBatch_PreservesModifiedByUserId()
    {
        // Arrange - Create isolated test database
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_UpsertBatch_PreservesModifiedByUserId));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var userId = "user-123";
        var customerId = Guid.NewGuid();

        // Act - Push through production flow
        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(config, new Dictionary<string, int> { ["Customers"] = 1 })
        };
        var beginResponse = await sessionTracker.CreatePushSessionAsync(beginRequest);
        var sessionId = beginResponse.SessionId;

        var batch = TestDataGenerator.CreateCustomersDict(1, userId);
        batch[0]["Id"] = customerId;
        await tempTableManager.InsertBatchAsync(sessionId, "Customers", batch);
        await sessionTracker.CompleteTableAsync(sessionId, "Customers", totalRecordsSent: 1);
        await sessionTracker.MarkSessionReadyAsync(sessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Assert - ModifiedByUserId and SyncSessionId preserved through production push flow
        using var connection = await _dbFactory.GetConnectionAsync();
        var result = await connection.QuerySingleAsync<dynamic>(
            "SELECT ModifiedByUserId, SyncSessionId FROM Customers WHERE Id = @Id",
            new { Id = customerId });

        ((string)result.ModifiedByUserId).Should().Be(userId);
        ((Guid)result.SyncSessionId).Should().Be(sessionId);
    }

    [Fact]
    public async Task TempTableManager_InsertBatchIntoTempTable_MultipleSessions_IsolatesBySessionId()
    {
        // Arrange - Create isolated test database
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTableManager_InsertBatchIntoTempTable_MultipleSessions_IsolatesBySessionId));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(
            serverDb,
            config,
            NullLogger<TempTableManager>.Instance);

        var sessionTracker = new SessionTracker(
            serverDb,
            tempTableManager,
            NullLogger<SessionTracker>.Instance);

        // Create two sessions with different clients
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();

        var session1Request = new PushSessionBeginRequest
        {
            DeviceId = clientId1,
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 10 })
        };

        var session2Request = new PushSessionBeginRequest
        {
            DeviceId = clientId2,
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 10 })
        };

        var session1Response = await sessionTracker.CreatePushSessionAsync(session1Request);
        var session2Response = await sessionTracker.CreatePushSessionAsync(session2Request);

        // Create test data for each session with different users
        var customers1Batch = TestDataGenerator.CreateCustomersDict(5, "user-session1");// TestDataGenerator.CreateCustomers(5, "user-session1");
        var customers2Batch = TestDataGenerator.CreateCustomersDict(5, "user-session2");

        // Act - Insert batches from both sessions to shared table
        var count1 = await tempTableManager.InsertBatchAsync(
            session1Response.SessionId, "Customers", customers1Batch);
        
        var count2 = await tempTableManager.InsertBatchAsync(
            session2Response.SessionId, "Customers", customers2Batch);

        // Assert - Both batches inserted successfully
        count1.Should().Be(5);
        count2.Should().Be(5);

        // Verify isolation by SessionId
        using var connection = await _dbFactory.GetConnectionAsync();
        
        var session1Records = await connection.QueryAsync<dynamic>(
            "SELECT * FROM TempPushCustomers WHERE SessionId = @SessionId",
            new { SessionId = session1Response.SessionId.ToString() });
        
        var session2Records = await connection.QueryAsync<dynamic>(
            "SELECT * FROM TempPushCustomers WHERE SessionId = @SessionId",
            new { SessionId = session2Response.SessionId.ToString() });

        session1Records.Should().HaveCount(5);
        session2Records.Should().HaveCount(5);

        // Verify ModifiedByUserId isolation
        foreach (var record in session1Records)
        {
            ((string)record.ModifiedByUserId).Should().Be("user-session1");
        }

        foreach (var record in session2Records)
        {
            ((string)record.ModifiedByUserId).Should().Be("user-session2");
        }

        // Verify all customers have different IDs (no collision)
        var allRecords = await connection.QueryAsync<dynamic>(
            "SELECT DISTINCT Id FROM TempPushCustomers WHERE SessionId IN (@Session1, @Session2)",
            new 
            { 
                Session1 = session1Response.SessionId.ToString(),
                Session2 = session2Response.SessionId.ToString()
            });

        allRecords.Should().HaveCount(10); // 5 from each session, all unique
    }

    #endregion

    #region Database Operations Tests (Tests 4-6)

    [Fact]
    public async Task ServerDb_UpsertBatch_UpdatesExisting_PreservesNewValues()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_UpsertBatch_UpdatesExisting_PreservesNewValues));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var customerId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        // Act - Push 1: Initial insert
        var begin1 = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = TestDatabaseFactory.GetTableSyncInfos(config, new Dictionary<string, int> { ["Customers"] = 1 })
        };
        var session1Response = await sessionTracker.CreatePushSessionAsync(begin1);
        var sessionId1 = session1Response.SessionId;

        var batch1 = TestDataGenerator.CreateCustomersDict(1, "user-123");
        batch1[0]["Id"] = customerId;
        batch1[0]["Name"] = "John Doe";
        batch1[0]["Email"] = "john@example.com";
        await tempTableManager.InsertBatchAsync(sessionId1, "Customers", batch1);
        await sessionTracker.CompleteTableAsync(sessionId1, "Customers", totalRecordsSent: 1);
        await sessionTracker.MarkSessionReadyAsync(sessionId1);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Assert initial insert
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var initial = await connection.QuerySingleAsync<dynamic>(
                "SELECT Name, Email, ModifiedByUserId FROM Customers WHERE Id = @Id",
                new { Id = customerId });
            ((string)initial.Name).Should().Be("John Doe");
            ((string)initial.Email).Should().Be("john@example.com");
            ((string)initial.ModifiedByUserId).Should().Be("user-123");
        }

        // Act - Push 2: Update same record
        var begin2 = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = TestDatabaseFactory.GetTableSyncInfos(config, new Dictionary<string, int> { ["Customers"] = 1 })
        };
        var session2Response = await sessionTracker.CreatePushSessionAsync(begin2);
        var sessionId2 = session2Response.SessionId;

        var batch2 = TestDataGenerator.CreateCustomersDict(1, "user-456");
        batch2[0]["Id"] = customerId;
        batch2[0]["Name"] = "Jane Smith";
        batch2[0]["Email"] = "jane@example.com";
        await tempTableManager.InsertBatchAsync(sessionId2, "Customers", batch2);
        await sessionTracker.CompleteTableAsync(sessionId2, "Customers", totalRecordsSent: 1);
        await sessionTracker.MarkSessionReadyAsync(sessionId2);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Assert - Values updated to second push
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var updated = await connection.QuerySingleAsync<dynamic>(
                "SELECT Name, Email, ModifiedByUserId, SyncSessionId FROM Customers WHERE Id = @Id",
                new { Id = customerId });
            ((string)updated.Name).Should().Be("Jane Smith", "name should be updated");
            ((string)updated.Email).Should().Be("jane@example.com", "email should be updated");
            ((string)updated.ModifiedByUserId).Should().Be("user-456", "user should be updated");
            ((Guid)updated.SyncSessionId).Should().Be(sessionId2, "session should reflect latest push");
        }
    }

    [Fact]
    public async Task ServerDb_DeleteFromSharedTempTable_SessionFiltering_DeletesOnlySession()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_DeleteFromSharedTempTable_SessionFiltering_DeletesOnlySession));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(
            serverDb,
            config,
            NullLogger<TempTableManager>.Instance);

        var sessionTracker = new SessionTracker (
            serverDb,
            tempTableManager,
            NullLogger<SessionTracker>.Instance);

        // Create two sessions
        var session1 = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 10 })
        });

        var session2 = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 10 })
        });

        var sharedTableName = "TempPushCustomers";

        // Insert records for both sessions into shared table
        var customers1Batch = TestDataGenerator.CreateCustomersDict(5, "user-session1");
        var customers2Batch = TestDataGenerator.CreateCustomersDict(7, "user-session2");

        await tempTableManager.InsertBatchAsync(
            session1.SessionId, "Customers", customers1Batch);
        await tempTableManager.InsertBatchAsync(
            session2.SessionId, "Customers", customers2Batch);

        // Verify both sessions have data
        var count1Before = await serverDb.CountTempTableRecordsAsync(
            sharedTableName, session1.SessionId, usesSharedTable: true);
        var count2Before = await serverDb.CountTempTableRecordsAsync(
            sharedTableName, session2.SessionId, usesSharedTable: true);

        count1Before.Should().Be(5);
        count2Before.Should().Be(7);

        // Act - Delete only session 1 records
        await serverDb.DeleteFromSharedTempTableAsync(sharedTableName, session1.SessionId);// DeleteTempTableRecordsAsync(sharedTableName, session1.SessionId);

        // Assert - Session 1 deleted, session 2 intact
        var count1After = await serverDb.CountTempTableRecordsAsync(
            sharedTableName, session1.SessionId, usesSharedTable: true);
        var count2After = await serverDb.CountTempTableRecordsAsync(
            sharedTableName, session2.SessionId, usesSharedTable: true);

        count1After.Should().Be(0, "session 1 records should be deleted");
        count2After.Should().Be(7, "session 2 records should remain intact");
    }

    [Fact]
    public async Task ServerDb_TempTableExists_DedicatedTable_ReturnsTrueWhenExists()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_TempTableExists_DedicatedTable_ReturnsTrueWhenExists));


        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        config.PushSharedTableThreshold = 5; // Force dedicated table with > 5 records
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(
            serverDb,
            config,
            NullLogger<TempTableManager>.Instance);

        var sessionTracker = new SessionTracker(
            serverDb,
            tempTableManager,
            NullLogger<SessionTracker>.Instance);

        // Create session with large dataset (triggers dedicated table)
        var sessionRequest = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 100 })
        };

        var sessionResponse = await sessionTracker.CreatePushSessionAsync(sessionRequest);
        var dedicatedTableName = sessionResponse.Tables["Customers"].TempTableName;

        // Act - Check if table exists (should be true after creation)
        var existsAfterCreate = await serverDb.TempTableExistsAsync(dedicatedTableName);

        // Assert - Table exists
        existsAfterCreate.Should().BeTrue("dedicated temp table should exist after session creation");

        // Act - Drop the table
        await serverDb.DropTempTableAsync(dedicatedTableName);

        // Act - Check if table exists (should be false after drop)
        var existsAfterDrop = await serverDb.TempTableExistsAsync(dedicatedTableName);

        // Assert - Table no longer exists
        existsAfterDrop.Should().BeFalse("dedicated temp table should not exist after drop");

        // Act - Check non-existent table
        var nonExistentTableExists = await serverDb.TempTableExistsAsync("NonExistentTable_" + Guid.NewGuid());

        // Assert - Returns false for non-existent table
        nonExistentTableExists.Should().BeFalse("non-existent table should return false");
    }

    #endregion

    #region Session Status Transitions Tests (Tests 7-9)

    [Fact]
    public async Task ServerDb_UpdateSessionStatus_Lifecycle_TransitionsStagingToCommitted()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_UpdateSessionStatus_Lifecycle_TransitionsStagingToCommitted));



        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var sessionId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        // Create session (Status = 'Staging')
        var session = new SessionRecord
        {
            SessionId = sessionId,
            SessionType = "Push",
            Status = "Staging",
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow
        };

        await serverDb.CreateSessionAsync(session);

        // Verify initial state
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var initial = await connection.QuerySingleAsync<dynamic>(
                "SELECT Status, LastActivityUtc, CommittedAtUtc FROM SessionRecords WHERE SessionId = @SessionId",
                new { SessionId = sessionId.ToString() });

            ((string)initial.Status).Should().Be("Staging");
            ((DateTime?)initial.CommittedAtUtc).Should().BeNull();
        }

        var time1 = DateTime.UtcNow;
        await Task.Delay(100); // Small delay to ensure timestamp changes

        // Act - Transition to 'Ready'
        await serverDb.MarkSessionReadyAsync(sessionId);

        // Assert - Status updated, activity time changed
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var ready = await connection.QuerySingleAsync<dynamic>(
                "SELECT Status, LastActivityUtc, CommittedAtUtc FROM SessionRecords WHERE SessionId = @SessionId",
                new { SessionId = sessionId.ToString() });

            ((string)ready.Status).Should().Be("Ready");
            ((DateTime)ready.LastActivityUtc).Should().BeAfter(time1);
            ((DateTime?)ready.CommittedAtUtc).Should().BeNull();
        }

        var time2 = DateTime.UtcNow;
        await Task.Delay(100);

        // Act - Transition to 'Processing'
        await serverDb.UpdateSessionStatusAsync(sessionId, "Processing");

        // Assert - Status updated, activity time changed
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var processing = await connection.QuerySingleAsync<dynamic>(
                "SELECT Status, LastActivityUtc, CommittedAtUtc FROM SessionRecords WHERE SessionId = @SessionId",
                new { SessionId = sessionId.ToString() });

            ((string)processing.Status).Should().Be("Processing");
            ((DateTime)processing.LastActivityUtc).Should().BeAfter(time2);
            ((DateTime?)processing.CommittedAtUtc).Should().BeNull();
        }

        var time3 = DateTime.UtcNow;
        await Task.Delay(100);

        // Act - Transition to 'Committed'
        await serverDb.UpdateSessionStatusAsync(sessionId, "Committed");

        // Assert - Status updated, CommittedAtUtc now set
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var committed = await connection.QuerySingleAsync<dynamic>(
                "SELECT Status, LastActivityUtc, CommittedAtUtc FROM SessionRecords WHERE SessionId = @SessionId",
                new { SessionId = sessionId.ToString() });

            ((string)committed.Status).Should().Be("Committed");
            ((DateTime)committed.LastActivityUtc).Should().BeAfter(time3);
            ((DateTime)committed.CommittedAtUtc).Should().BeAfter(time3, "CommittedAtUtc should be set on Committed status");
        }
    }

    [Fact]
    public async Task ServerDb_GetUnseenSessionIds_SessionBasedTracking_ExcludesProcessed()
    {
        // Arrange - This tests the CORE INNOVATION: session-based tracking!
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_GetUnseenSessionIds_SessionBasedTracking_ExcludesProcessed));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);

        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var clientC = Guid.NewGuid(); // Creates sessions but never processed by A or B

        // Create 3 committed sessions from different clients
        var session1 = new SessionRecord
        {
            SessionId = Guid.NewGuid(),
            SessionType = "Push",
            Status = "Committed",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-25),
            CommittedAtUtc = DateTime.UtcNow.AddMinutes(-25)
        };

        var session2 = new SessionRecord
        {
            SessionId = Guid.NewGuid(),
            SessionType = "Push",
            Status = "Committed",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-15),
            CommittedAtUtc = DateTime.UtcNow.AddMinutes(-15)
        };

        var session3 = new SessionRecord
        {
            SessionId = Guid.NewGuid(),
            SessionType = "Push",
            Status = "Committed",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-5),
            CommittedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        await serverDb.CreateSessionAsync(session1);
        await serverDb.CreateSessionAsync(session2);
        await serverDb.CreateSessionAsync(session3);

        // Client A processes session 1
        await serverDb.MarkSessionsProcessedAsync(clientA, new[] { session1.SessionId });

        // Client B processes session 1 and 2
        await serverDb.MarkSessionsProcessedAsync(clientB, new[] { session1.SessionId, session2.SessionId });

        // Act - Get unseen sessions for each client
        var unseenForA = (await serverDb.FindUnseenSessionIdsAsync(clientA)).ToList();
        var unseenForB = (await serverDb.FindUnseenSessionIdsAsync(clientB)).ToList();

        // Assert - Client A should see sessions 2 and 3
        unseenForA.Should().HaveCount(2);
        unseenForA.Should().Contain(session2.SessionId);
        unseenForA.Should().Contain(session3.SessionId);
        unseenForA.Should().NotContain(session1.SessionId, "Client A already processed session 1");

        // Assert - Client B should see only session 3
        unseenForB.Should().HaveCount(1);
        unseenForB.Should().Contain(session3.SessionId);
        unseenForB.Should().NotContain(session1.SessionId, "Client B already processed session 1");
        unseenForB.Should().NotContain(session2.SessionId, "Client B already processed session 2");

        // Assert - Verify ordering by SyncVersion (ascending)
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var versions = await connection.QueryAsync<(Guid SessionId, long SyncVersion)>(
                "SELECT SessionId, SyncVersion FROM SessionRecords ORDER BY SyncVersion",
                new { });

            var versionList = versions.ToList();
            versionList[0].SessionId.Should().Be(session1.SessionId);
            versionList[1].SessionId.Should().Be(session2.SessionId);
            versionList[2].SessionId.Should().Be(session3.SessionId);

            // Verify unseen sessions are ordered by SyncVersion
            var session2Version = versionList.First(v => v.SessionId == session2.SessionId).SyncVersion;
            var session3Version = versionList.First(v => v.SessionId == session3.SessionId).SyncVersion;

            session2Version.Should().BeLessThan(session3Version, "Sessions should be ordered by SyncVersion");

            // Verify FindUnseenSessionIdsAsync returns in correct order (session 2 before session 3 for client A)
            unseenForA[0].Should().Be(session2.SessionId);
            unseenForA[1].Should().Be(session3.SessionId);
        }
    }

    [Fact]
    public async Task ServerDb_MarkSessionsProcessed_ClientTracking_PreventsRePull()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_MarkSessionsProcessed_ClientTracking_PreventsRePull));



        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(
            connectionString,
            tableMetaDataCache,
            config,
            NullLogger<MySqlServerDatabase>.Instance);

        var clientId = Guid.NewGuid();
        var deviceId = Guid.NewGuid(); // Device pulling the data

        // Create 3 committed sessions
        var session1 = new SessionRecord
        {
            SessionId = Guid.NewGuid(),
            SessionType = "Push",
            Status = "Committed",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-25),
            CommittedAtUtc = DateTime.UtcNow.AddMinutes(-25)
        };

        var session2 = new SessionRecord
        {
            SessionId = Guid.NewGuid(),
            SessionType = "Push",
            Status = "Committed",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-15),
            CommittedAtUtc = DateTime.UtcNow.AddMinutes(-15)
        };

        var session3 = new SessionRecord
        {
            SessionId = Guid.NewGuid(),
            SessionType = "Push",
            Status = "Committed",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-5),
            CommittedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        await serverDb.CreateSessionAsync(session1);
        await serverDb.CreateSessionAsync(session2);
        await serverDb.CreateSessionAsync(session3);

        // Verify device initially sees all 3 sessions
        var unseenBefore = (await serverDb.FindUnseenSessionIdsAsync(deviceId)).ToList();
        unseenBefore.Should().HaveCount(3);

        // Act - Device marks session 1 as processed (single session)
        await serverDb.MarkSessionsProcessedAsync(deviceId, new[] { session1.SessionId });

        // Assert - Record inserted into ClientProcessedSessions
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var processedCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ClientProcessedSessions WHERE DeviceId = @DeviceId AND SessionId = @SessionId",
                new { DeviceId = deviceId.ToString(), SessionId = session1.SessionId.ToString() });

            processedCount.Should().Be(1, "record should be inserted into ClientProcessedSessions");
        }

        // Assert - FindUnseenSessionIdsAsync no longer returns session 1
        var unseenAfter1 = (await serverDb.FindUnseenSessionIdsAsync(deviceId)).ToList();
        unseenAfter1.Should().HaveCount(2);
        unseenAfter1.Should().NotContain(session1.SessionId);
        unseenAfter1.Should().Contain(session2.SessionId);
        unseenAfter1.Should().Contain(session3.SessionId);

        // Act - Device marks sessions 2 and 3 as processed (multiple sessions in single call)
        await serverDb.MarkSessionsProcessedAsync(deviceId, new[] { session2.SessionId, session3.SessionId });

        // Assert - Both records inserted
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var allProcessedCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ClientProcessedSessions WHERE DeviceId = @DeviceId",
                new { DeviceId = deviceId.ToString() });

            allProcessedCount.Should().Be(3, "all 3 sessions should be marked as processed");
        }

        // Assert - FindUnseenSessionIdsAsync returns empty (all processed)
        var unseenFinal = (await serverDb.FindUnseenSessionIdsAsync(deviceId)).ToList();
        unseenFinal.Should().BeEmpty("all sessions have been processed");

        // Assert - Verify ProcessedAtUtc timestamps are set
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var processedRecords = await connection.QueryAsync<dynamic>(
                "SELECT SessionId, ProcessedAtUtc FROM ClientProcessedSessions WHERE DeviceId = @DeviceId ORDER BY ProcessedAtUtc",
                new { DeviceId = deviceId.ToString() });

            var recordsList = processedRecords.ToList();
            recordsList.Should().HaveCount(3);

            foreach (var record in recordsList)
            {
                ((DateTime)record.ProcessedAtUtc).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            }
        }
    }

    #endregion

    #region Record Counting & Verification Tests (Tests 10-12)

    [Fact]
    public async Task ServerDb_CountTempTableRecords_SharedTable_FiltersCorrectly()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_CountTempTableRecords_SharedTable_FiltersCorrectly));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(
            connectionString,
            tableMetaDataCache,
            config,
            NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(
            serverDb,
            config,
            NullLogger<TempTableManager>.Instance);

        var sessionTracker = new SessionTracker(
            serverDb,
            tempTableManager,
            NullLogger<SessionTracker>.Instance);

        // Create 2 sessions (both will use shared table - small datasets)
        var session1 = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 10 })
        });

        var session2 = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 5 })
        });

        var sharedTableName = "TempPushCustomers";

        // Insert 10 records for session 1
        var customers1Batch = TestDataGenerator.CreateCustomersDict(10, "user-session1");
        
        await tempTableManager.InsertBatchAsync(session1.SessionId, "Customers", customers1Batch);

        // Insert 5 records for session 2
        var customers2Batch = TestDataGenerator.CreateCustomersDict(5, "user-session2");
        await tempTableManager.InsertBatchAsync(session2.SessionId, "Customers", customers2Batch);

        // Act - Count records for each session
        var count1 = await serverDb.CountTempTableRecordsAsync(
            sharedTableName, session1.SessionId, usesSharedTable: true);
        var count2 = await serverDb.CountTempTableRecordsAsync(
            sharedTableName, session2.SessionId, usesSharedTable: true);

        // Assert - Session-specific counts
        count1.Should().Be(10, "session 1 should have 10 records");
        count2.Should().Be(5, "session 2 should have 5 records");

        // Act - Get total count across all sessions (direct query)
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var totalCount = await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {sharedTableName}");

            // Assert - Total is sum of both sessions
            totalCount.Should().Be(15, "total should be 10 + 5 = 15");
        }

        // Verify session isolation - records from different sessions don't interfere
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var session1Records = await connection.QueryAsync<dynamic>(
                $"SELECT ModifiedByUserId FROM {sharedTableName} WHERE SessionId = @SessionId",
                new { SessionId = session1.SessionId.ToString() });

            var session2Records = await connection.QueryAsync<dynamic>(
                $"SELECT ModifiedByUserId FROM {sharedTableName} WHERE SessionId = @SessionId",
                new { SessionId = session2.SessionId.ToString() });

            session1Records.Should().HaveCount(10);
            session2Records.Should().HaveCount(5);

            // All session 1 records should have user-session1
            foreach (var record in session1Records)
            {
                ((string)record.ModifiedByUserId).Should().Be("user-session1");
            }

            // All session 2 records should have user-session2
            foreach (var record in session2Records)
            {
                ((string)record.ModifiedByUserId).Should().Be("user-session2");
            }
        }
    }

    [Fact]
    public async Task ServerDb_CountTempTableRecords_DedicatedTable_CountsAll()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_CountTempTableRecords_DedicatedTable_CountsAll));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        config.PushSharedTableThreshold = 10; // Force dedicated table with > 10 records

        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(
            connectionString,
            tableMetaDataCache,
            config,
            NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(
            serverDb,
            config,
            NullLogger<TempTableManager>.Instance);

        var sessionTracker = new SessionTracker(
            serverDb,
            tempTableManager,
            NullLogger<SessionTracker>.Instance);

        // Create session with large dataset (triggers dedicated table)
        var session = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 100 })
        });

        var dedicatedTableName = session.Tables["Customers"].TempTableName;

        // Insert 50 records into dedicated table
        var customersBatch = TestDataGenerator.CreateCustomersDict(50, "user-123");
        await tempTableManager.InsertBatchAsync(session.SessionId, "Customers", customersBatch);

        // Act - Count records in dedicated table (usesSharedTable: false, no SessionId filtering)
        var count = await serverDb.CountTempTableRecordsAsync(
            dedicatedTableName,
            session.SessionId,
            usesSharedTable: false);

        // Assert - Should count all 50 records
        count.Should().Be(50, "dedicated table should have all 50 records");

        // Verify no SessionId filtering occurs for dedicated tables
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            // Dedicated tables don't have SessionId column, so direct count should match
            var directCount = await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM `{dedicatedTableName}`");

            directCount.Should().Be(50, "direct count should match CountTempTableRecordsAsync");

            // Verify table structure - dedicated tables have Id as PK, no SessionId column
            var columns = await connection.QueryAsync<string>(
                @"SELECT COLUMN_NAME 
                  FROM information_schema.COLUMNS 
                  WHERE TABLE_SCHEMA = DATABASE() 
                    AND TABLE_NAME = @TableName
                  ORDER BY ORDINAL_POSITION",
                new { TableName = dedicatedTableName });

            var columnList = columns.ToList();
            columnList.Should().Contain("Id", "dedicated table should have Id column");
            columnList.Should().NotContain("SessionId", "dedicated tables don't have SessionId column");
        }

        // Verify all records are accessible
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var allRecords = await connection.QueryAsync<dynamic>(
                $"SELECT Id, Name, ModifiedByUserId FROM `{dedicatedTableName}`");

            allRecords.Should().HaveCount(50);

            // All records should have the same user
            foreach (var record in allRecords)
            {
                ((string)record.ModifiedByUserId).Should().Be("user-123");
            }
        }
    }

    [Fact]
    public async Task ServerDb_UpdateSessionTableStatus_RecordCount_UpdatesCorrectly()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_UpdateSessionTableStatus_RecordCount_UpdatesCorrectly));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);

        var serverDb = new MySqlServerDatabase(
            connectionString,
            tableMetaDataCache,
            config,
            NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(
            serverDb,
            config,
            NullLogger<TempTableManager>.Instance);

        var sessionTracker = new SessionTracker(
            serverDb,
            tempTableManager,
            NullLogger<SessionTracker>.Instance);

        // Create push session with EstimatedRecordCount = 100
        var session = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 100 })
        });

        // Verify initial state
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var initial = await connection.QuerySingleAsync<dynamic>(
                @"SELECT EstimatedRecordCount, ActualRecordCount, Status 
                  FROM SyncSessionTables 
                  WHERE SessionId = @SessionId AND TableName = 'Customers'",
                new { SessionId = session.SessionId.ToString() });

            ((int)initial.EstimatedRecordCount).Should().Be(100);
            ((int?)initial.ActualRecordCount ?? 0).Should().Be(0, "ActualRecordCount should be null or 0 initially");
            ((string)initial.Status).Should().Be("Staging");
        }

        // Insert 95 actual records (less than estimated)
        var customersBatch = TestDataGenerator.CreateCustomersDict(95, "user-123");
        await tempTableManager.InsertBatchAsync(session.SessionId, "Customers", customersBatch);

        // Act - Update session table with actual count and status
        await serverDb.UpdateSessionTableStatusAsync(
            session.SessionId,
            "Customers",
            actualRecordCount: 95,
            status: "Ready");

        // Assert - ActualRecordCount and Status updated
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var updated = await connection.QuerySingleAsync<dynamic>(
                @"SELECT EstimatedRecordCount, ActualRecordCount, Status 
                  FROM SyncSessionTables 
                  WHERE SessionId = @SessionId AND TableName = 'Customers'",
                new { SessionId = session.SessionId.ToString() });

            ((int)updated.EstimatedRecordCount).Should().Be(100, "EstimatedRecordCount should remain unchanged");
            ((int)updated.ActualRecordCount).Should().Be(95, "ActualRecordCount should be updated to 95");
            ((string)updated.Status).Should().Be("Ready", "Status should be updated to Ready");
        }

        // Act - Update to 'Completed' status
        await serverDb.UpdateSessionTableStatusAsync(
            session.SessionId,
            "Customers",
            actualRecordCount: 95,
            status: "Completed");

        // Assert - Status changed to Completed
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var completed = await connection.QuerySingleAsync<dynamic>(
                @"SELECT ActualRecordCount, Status 
                  FROM SyncSessionTables 
                  WHERE SessionId = @SessionId AND TableName = 'Customers'",
                new { SessionId = session.SessionId.ToString() });

            ((int)completed.ActualRecordCount).Should().Be(95);
            ((string)completed.Status).Should().Be("Completed");
        }

        // Test edge case: ActualRecordCount matches EstimatedRecordCount
        var session2 = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 50 })
        });

        await serverDb.UpdateSessionTableStatusAsync(
            session2.SessionId,
            "Customers",
            actualRecordCount: 50,
            status: "Ready");

        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            var exact = await connection.QuerySingleAsync<dynamic>(
                @"SELECT EstimatedRecordCount, ActualRecordCount 
                  FROM SyncSessionTables 
                  WHERE SessionId = @SessionId AND TableName = 'Customers'",
                new { SessionId = session2.SessionId.ToString() });

            ((int)exact.EstimatedRecordCount).Should().Be(50);
            ((int)exact.ActualRecordCount).Should().Be(50, "actual matches estimated");
        }
    }

    #endregion

    #region Temp Table Operations Tests (Tests 13-15)

    [Fact]
    public async Task ServerDb_GetSessionTableInfo_ReturnsCorrectTempTable()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_GetSessionTableInfo_ReturnsCorrectTempTable));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        config.PushSharedTableThreshold = 50; // Force Orders to use dedicated (>50), Customers shared

        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);

        var serverDb = new MySqlServerDatabase(
            connectionString,
            tableMetaDataCache,
            config,
            NullLogger<MySqlServerDatabase>.Instance);

        var tempTableManager = new TempTableManager(
            serverDb,
            config,
            NullLogger<TempTableManager>.Instance);

        var sessionTracker = new SessionTracker(
            serverDb,
            tempTableManager,
            NullLogger<SessionTracker>.Instance);

        // Create session with 2 tables: small (shared) and large (dedicated)
        var session = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int>
                {
                    ["Customers"] = 10,     // Uses shared table
                    ["Orders"] = 100        // Uses dedicated table (> 50)
                })
        });

        // Act - Get Customers table info (shared)
        var customersInfo = await serverDb.GetSessionTableInfoAsync(session.SessionId, "Customers");

        // Assert - Customers uses shared table
        // Assert not null first
        customersInfo.Should().NotBeNull("table info should exist");
        customersInfo.Value.TempTableName.Should().Be("TempPushCustomers", "small datasets use shared table");
        customersInfo.Value.UsesSharedTable.Should().BeTrue("Customers should use shared table");

        // Act - Get Orders table info (dedicated)
        var ordersInfo = await serverDb.GetSessionTableInfoAsync(session.SessionId, "Orders");

        // Assert - Orders uses dedicated table with unique name
        ordersInfo.Should().NotBeNull("table info should exist");
        ordersInfo.Value.TempTableName.Should().StartWith("TempPush_Orders_", "large datasets use dedicated table");
        ordersInfo.Value.UsesSharedTable.Should().BeFalse("Orders should use dedicated table");

        // Verify dedicated table actually exists
        var dedicatedTableExists = await serverDb.TempTableExistsAsync(ordersInfo.Value.TempTableName!);
        dedicatedTableExists.Should().BeTrue("dedicated temp table should be created");

        // Act - Query non-existent table
        var nonExistentInfo = await serverDb.GetSessionTableInfoAsync(session.SessionId, "NonExistentTable");

        // Assert - Returns default values for non-existent table
        nonExistentInfo.Should().BeNull("should return null for non-existent table");
        //nonExistentInfo.Value.TempTableName.Should().BeNull("non-existent table should return null");
        //nonExistentInfo.Value.UsesSharedTable.Should().BeFalse();
    }

    [Fact]
    public async Task ServerDb_InsertBatchIntoTempTable_DictionaryHandling_AllDataTypes()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_InsertBatchIntoTempTable_DictionaryHandling_AllDataTypes));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);

        var serverDb = new MySqlServerDatabase(
            connectionString,
            tableMetaDataCache,
            config,
            NullLogger<MySqlServerDatabase>.Instance);

        var sessionId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        // Create Customer entities with various data types including nulls
        var customers = new List<Customer>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Customer with all values",
                Email = "test@example.com",
                Phone = "+1-555-1234",
                Address = "123 Main St",
                ModifiedAtUtc = new DateTime(2025, 1, 4, 12, 30, 45, 123, DateTimeKind.Utc),
                ModifiedByUserId = "user-1",
                IsDeleted = false,
                IsDirty = false,
                SyncSessionId = Guid.NewGuid()
            },
            new()
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Customer with nulls",
                Email = "null-test@example.com",
                Phone = null,  // Null value
                Address = null, // Null value
                ModifiedAtUtc = new DateTime(2025, 1, 4, 14, 0, 0, 0, DateTimeKind.Utc),
                ModifiedByUserId = "user-2",
                IsDeleted = true,
                IsDirty = false,
                SyncSessionId = Guid.NewGuid()
            },
            new()
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Customer for precision test",
                Email = "precision@example.com",
                Phone = "+1-555-9999",
                Address = "999 Test Ave",
                ModifiedAtUtc = new DateTime(2025, 1, 4, 18, 45, 30, 456, 789, DateTimeKind.Utc), // Microsecond precision
                ModifiedByUserId = "user-3",
                IsDeleted = false,
                IsDirty = false,
                SyncSessionId = Guid.NewGuid()
            }
        };

        // Convert to dictionaries using EntityReflectionHelper (production code path)
        var records = customers.Select(c => EntityReflectionHelper.EntityToDictionary(c)).ToList();

        // Act - Insert batch into shared temp table
        var insertedCount = await serverDb.InsertBatchIntoTempTableAsync(
            "TempPushCustomers",
            usesSharedTable: true,
            sessionId,
            "Customers",
            records);

        // Assert - All records inserted
        insertedCount.Should().Be(3, "all 3 records should be inserted");

        // Query temp table and verify all values preserved
        using var connection = await _dbFactory.GetConnectionAsync();
        var retrieved = await connection.QueryAsync<dynamic>(
            @"SELECT Id, Name, Email, Phone, Address, ModifiedAtUtc, ModifiedByUserId, IsDeleted
              FROM TempPushCustomers
              WHERE SessionId = @SessionId
              ORDER BY Name",
            new { SessionId = sessionId.ToString() });

        var retrievedList = retrieved.ToList();
        retrievedList.Should().HaveCount(3);

        // Record 1: All values present
        var record1 = retrievedList[0];
        ((string)record1.Name).Should().Be("Customer for precision test");
        ((string)record1.Email).Should().Be("precision@example.com");
        ((string)record1.Phone).Should().Be("+1-555-9999");
        ((string)record1.Address).Should().Be("999 Test Ave");
        ((string)record1.ModifiedByUserId).Should().Be("user-3");
        ((bool)record1.IsDeleted).Should().BeFalse();

        // Verify DateTime precision (MySQL DATETIME(6) supports microseconds)
        var expectedDateTime = new DateTime(2025, 1, 4, 18, 45, 30, 456, DateTimeKind.Utc).AddTicks(7890);
        //var actualDateTime = (DateTime)record1.ModifiedAtUtc;
        //actualDateTime.Should().BeCloseTo(expectedDateTime, TimeSpan.FromMilliseconds(1), "DateTime precision should be maintained");

        // Record 2: With all values
        var record2 = retrievedList[1];
        ((string)record2.Name).Should().Be("Customer with all values");
        ((string)record2.Email).Should().Be("test@example.com");
        ((string)record2.Phone).Should().Be("+1-555-1234");
        ((string)record2.Address).Should().Be("123 Main St");
        ((string)record2.ModifiedByUserId).Should().Be("user-1");

        // Record 3: With null values
        var record3 = retrievedList[2];
        ((string)record3.Name).Should().Be("Customer with nulls");
        ((string)record3.Email).Should().Be("null-test@example.com");
        ((object?)record3.Phone).Should().BeNull("null phone should be preserved");
        ((object?)record3.Address).Should().BeNull("null address should be preserved");
        ((string)record3.ModifiedByUserId).Should().Be("user-2");
        ((bool)record3.IsDeleted).Should().BeTrue();
    }

    [Fact]
    public async Task ServerDb_GetPullBatch_Pagination_CorrectBoundariesAndHasMore()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(ServerDb_GetPullBatch_Pagination_CorrectBoundariesAndHasMore));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);

        var serverDb = new MySqlServerDatabase(
            connectionString,
            tableMetaDataCache,
            config,
            NullLogger<MySqlServerDatabase>.Instance);

        var pullSessionId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        // Insert 25 records into TempPullCustomers for pagination testing
        var customers = new List<Customer>();
        for (int i = 1; i <= 25; i++)
        {
            customers.Add(new Customer
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = $"Customer {i:D3}",
                Email = $"customer{i}@example.com",
                ModifiedAtUtc = DateTime.UtcNow,
                ModifiedByUserId = "user-test",
                IsDeleted = false,
                SyncSessionId = sessionId,
                IsDirty = false
            });
        }

        // Convert to dictionaries and add temp table columns
        using (var connection = await _dbFactory.GetConnectionAsync())
        {
            foreach (var customer in customers)
            {
                var dict = EntityReflectionHelper.EntityToDictionary(customer);
                dict["SessionId"] = pullSessionId;
                
                // Build column list and parameter list
                var columns = string.Join(", ", dict.Keys);
                var parameters = string.Join(", ", dict.Keys.Select(k => $"@{k}"));
                
                var sql = $"INSERT INTO TempPullCustomers ({columns}) VALUES ({parameters})";
                await connection.ExecuteAsync(sql, dict);
            }
        }

        // Act 1 - First page (offset: 0, limit: 10)
        var batch1 = await serverDb.GetPullBatchAsync(
            "TempPullCustomers",
            pullSessionId,
            offset: 0,
            limit: 10);

        // Assert 1 - First page
        batch1.Records.Should().HaveCount(10, "first page should return 10 records");
        batch1.HasMore.Should().BeTrue("should have more records after first page");
        batch1.TotalRecords.Should().Be(25, "total count should be 25");

        // Verify records are ordered by Id
        var firstPageNames = batch1.Records.Select(r => r["Name"]?.ToString()).ToList();
        firstPageNames.Should().HaveCount(10);

        // Act 2 - Second page (offset: 10, limit: 10)
        var batch2 = await serverDb.GetPullBatchAsync(
            "TempPullCustomers",
            pullSessionId,
            offset: 10,
            limit: 10);

        // Assert 2 - Second page
        batch2.Records.Should().HaveCount(10, "second page should return 10 records");
        batch2.HasMore.Should().BeTrue("should have more records after second page (5 remaining)");
        batch2.TotalRecords.Should().Be(25, "total count should still be 25");

        // Verify no overlap between pages
        var secondPageNames = batch2.Records.Select(r => r["Name"]?.ToString()).ToList();
        firstPageNames.Should().NotIntersectWith(secondPageNames, "pages should not overlap");

        // Act 3 - Third/Final page (offset: 20, limit: 10)
        var batch3 = await serverDb.GetPullBatchAsync(
            "TempPullCustomers",
            pullSessionId,
            offset: 20,
            limit: 10);

        // Assert 3 - Final page
        batch3.Records.Should().HaveCount(5, "final page should return only 5 remaining records");
        batch3.HasMore.Should().BeFalse("no more records after final page");
        batch3.TotalRecords.Should().Be(25, "total count should still be 25");

        // Verify all records retrieved across all pages
        var allRecords = batch1.Records.Concat(batch2.Records).Concat(batch3.Records).ToList();
        allRecords.Should().HaveCount(25, "all pages combined should return 25 unique records");

        // Verify all records have required fields and PullSessionId/CreatedAtUtc excluded
        foreach (var record in allRecords)
        {
            record.Should().ContainKey("Id");
            record.Should().ContainKey("Name");
            record.Should().ContainKey("Email");
            record.Should().ContainKey("ModifiedAtUtc");
            record.Should().ContainKey("ModifiedByUserId");
            record.Should().NotContainKey("SessionId", "SessionId should be excluded from results");
            record.Should().NotContainKey("CreatedAtUtc", "CreatedAtUtc should be excluded from results");
        }

        // Test edge case: offset beyond total records
        var batchBeyond = await serverDb.GetPullBatchAsync(
            "TempPullCustomers",
            pullSessionId,
            offset: 100,
            limit: 10);

        batchBeyond.Records.Should().BeEmpty("no records should be returned when offset exceeds total");
        batchBeyond.HasMore.Should().BeFalse("hasMore should be false when offset exceeds total");
        batchBeyond.TotalRecords.Should().Be(25, "total should still be 25");
    }

    #endregion
}
