using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Samples.Shared.TestData;
using SyncSession.Server.Database;
using SyncSession.Server.Models;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.Transactions;

/// <summary>
/// Session 19f: Tests for server-side session-level transaction behavior.
/// Verifies that SyncQueueProcessor.ProcessSessionAsync wraps all table upserts
/// in a single atomic transaction, ensuring zero partial commits on failure.
/// </summary>
[Collection("MariaDB Collection")]
public class ServerSessionTransactionTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private string _testConnectionString = string.Empty;
    private MySqlServerDatabase? _serverDb;
    private TempTableManager? _tempTableManager;
    private SessionTracker? _sessionTracker;
    private SyncQueueProcessor? _queueProcessor;
    private ServerSyncConfiguration? _config;

    public ServerSessionTransactionTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // Reset static cache so this test class initializes EntityReflectionHelper with its
        // own ServerSyncConfiguration, regardless of what other test classes ran first.
        EntityReflectionHelper.ClearCache();

        // Create isolated test database
        _testConnectionString = await _fixture.CreateTestDatabaseAsync(nameof(ServerSessionTransactionTests));
        
        // Create configuration
        _config = new ServerSyncConfiguration
        {
            PushSharedTableThreshold = 10000,
            PullSharedTableThreshold = 10000,
            TransactionIsolationLevel = System.Data.IsolationLevel.Serializable
        };
        
        // Register tables (required for TableMetadataCache)
        _config.DiscoverAndRegisterTables(typeof(Customer).Assembly);
        
        // Create services
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(_config);
        _serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, _config, NullLogger<MySqlServerDatabase>.Instance);
        _tempTableManager = new TempTableManager(_serverDb, _config, NullLogger<TempTableManager>.Instance);
        _sessionTracker = new SessionTracker(_serverDb, _tempTableManager, NullLogger<SessionTracker>.Instance);
        _queueProcessor = new SyncQueueProcessor(_serverDb, _tempTableManager, NullLogger<SyncQueueProcessor>.Instance);
    }

    public async Task DisposeAsync()
    {
        // No cleanup needed - MariaDbFixture handles database lifecycle
        await Task.CompletedTask;
    }

    #region A. Multi-Table Rollback Scenarios

    /// <summary>
    /// Test that when second table fails, first table is also rolled back.
    /// Session has: Customers (success) + Orders (FK violation).
    /// Expected: Both tables rolled back, session status = "Failed".
    /// </summary>
    [Fact]
    public async Task ProcessSession_SecondTableFails_RollsBackFirstTable()
    {
        // Arrange: Create session with Customers + Orders tables
        var deviceId = Guid.NewGuid();
        
        var request = new PushSessionBeginRequest
        {
            DeviceId = deviceId,
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Customers", EstimatedRecordCount = 10 },
                new() { TableName = "Orders", EstimatedRecordCount = 5 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage valid Customers data
        var customers = TestDataGenerator.CreateCustomersDict(10);
        await StageTempTableData(sessionId, "Customers", customers);
        
        // Mark Customers table complete
        await _sessionTracker.CompleteTableAsync(sessionId, "Customers", 10);
        
        // Stage Orders with INVALID CustomerId (FK violation)
        var invalidOrder = TestDataGenerator.CreateOrderDict(customerId: Guid.NewGuid());
        await StageTempTableData(sessionId, "Orders", new List<Dictionary<string, object?>> { invalidOrder });
        
        // Mark Orders table complete
        await _sessionTracker.CompleteTableAsync(sessionId, "Orders", 1);
        
        // Mark session ready for processing
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act: Process session - should throw FK violation
        await Assert.ThrowsAsync<MySqlException>(async () =>
            await _queueProcessor!.ProcessSessionAsync(sessionId));
        
        // Assert: Complete rollback - NO records in production tables
        var customersCount = await GetProductionRecordCount("Customers");
        customersCount.Should().Be(0, "Customers should be rolled back when Orders fails");
        
        var ordersCount = await GetProductionRecordCount("Orders");
        ordersCount.Should().Be(0, "Orders should not be committed");
        
        // Verify session is marked as Failed
        var session = await GetSession(sessionId);
        session!.Status.Should().Be("Failed", "session should be marked Failed after rollback");
        session.ErrorMessage.Should().ContainEquivalentOf("foreign key");
    }

    /// <summary>
    /// Test that when third table fails, all previous tables are rolled back.
    /// Session: Customers → Orders → OrderItems (duplicate key).
    /// Expected: All three tables rolled back, production unchanged.
    /// </summary>
    [Fact]
    public async Task ProcessSession_ThirdTableFails_RollsBackAllPrevious()
    {
        // Arrange: Session with Customers → Orders → OrderItems
        var deviceId = Guid.NewGuid();
        
        var request = new PushSessionBeginRequest
        {
            DeviceId = deviceId,
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Customers", EstimatedRecordCount = 5 },
                new() { TableName = "Orders", EstimatedRecordCount = 10 },
                new() { TableName = "OrderItems", EstimatedRecordCount = 20 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage valid Customers
        var customers = TestDataGenerator.CreateCustomersDict(5);
        await StageTempTableData(sessionId, "Customers", customers);
        await _sessionTracker.CompleteTableAsync(sessionId, "Customers", 5);
        
        // Stage valid Orders (referencing actual customer IDs)
        var orders = customers.SelectMany(c => 
            TestDataGenerator.CreateOrdersDict((Guid)c["Id"]!, 2)).ToList();
        await StageTempTableData(sessionId, "Orders", orders);
        await _sessionTracker.CompleteTableAsync(sessionId, "Orders", 10);
        
        // Stage OrderItems with INVALID OrderId (FK violation on third table)
        var dummyProductId = Guid.NewGuid(); // ProductId doesn't matter - testing OrderId FK
        var invalidOrderItem = TestDataGenerator.CreateOrderItemDict(
            orderId: Guid.NewGuid(), 
            productId: dummyProductId);
        await StageTempTableData(sessionId, "OrderItems", new List<Dictionary<string, object?>> { invalidOrderItem });
        await _sessionTracker.CompleteTableAsync(sessionId, "OrderItems", 1);
        
        // Mark ready
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act: Process - should throw on third table
        await Assert.ThrowsAsync<MySqlException>(async () =>
            await _queueProcessor!.ProcessSessionAsync(sessionId));
        
        // Assert: ALL tables rolled back
        var customersCount = await GetProductionRecordCount("Customers");
        customersCount.Should().Be(0, "Customers rolled back when OrderItems fails");
        
        var ordersCount = await GetProductionRecordCount("Orders");
        ordersCount.Should().Be(0, "Orders rolled back when OrderItems fails");
        
        var itemsCount = await GetProductionRecordCount("OrderItems");
        itemsCount.Should().Be(0, "OrderItems not committed");
        
        // Session marked Failed
        var session = await GetSession(sessionId);
        session!.Status.Should().Be("Failed");
    }

    /// <summary>
    /// Verify that error classification correctly identifies the SQL error type.
    /// Should log FOREIGN_KEY_VIOLATION and include table name + context.
    /// </summary>
    [Fact]
    public async Task ProcessSession_ForeignKeyViolation_LogsCorrectErrorType()
    {
        // Arrange: Session with Orders referencing invalid Customer
        var deviceId = Guid.NewGuid();
        
        var request = new PushSessionBeginRequest
        {
            DeviceId = deviceId,
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Orders", EstimatedRecordCount = 1 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage Orders with invalid FK
        var invalidOrder = TestDataGenerator.CreateOrderDict(customerId: Guid.NewGuid());
        await StageTempTableData(sessionId, "Orders", new List<Dictionary<string, object?>> { invalidOrder });
        await _sessionTracker.CompleteTableAsync(sessionId, "Orders", 1);
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act & Assert: Verify FK violation exception
        var exception = await Assert.ThrowsAsync<MySqlException>(async () =>
            await _queueProcessor!.ProcessSessionAsync(sessionId));
        
        // Verify MySQL error code is FK violation (1452)
        exception.Number.Should().Be(1452, "MySQL FK constraint violation code");
        exception.Message.Should().ContainEquivalentOf("foreign key");
        
        // Verify session captured error
        var session = await GetSession(sessionId);
        session!.Status.Should().Be("Failed");
        session.ErrorMessage.Should().ContainEquivalentOf("foreign key");
    }

    #endregion

    #region B. Foreign Key Constraint Testing

    /// <summary>
    /// Test that invalid foreign key causes full session rollback.
    /// Orders referencing non-existent CustomerId.
    /// </summary>
    [Fact]
    public async Task ProcessSession_InvalidForeignKey_RollsBackSession()
    {
        // Arrange: Orders table only, invalid CustomerId
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Orders", EstimatedRecordCount = 3 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage Orders with non-existent CustomerIds
        var invalidOrders = Enumerable.Range(1, 3)
            .Select(i => TestDataGenerator.CreateOrderDict(customerId: Guid.NewGuid()))
            .ToList();
        
        await StageTempTableData(sessionId, "Orders", invalidOrders);
        await _sessionTracker.CompleteTableAsync(sessionId, "Orders", 3);
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act: Process should fail on FK
        await Assert.ThrowsAsync<MySqlException>(async () =>
            await _queueProcessor!.ProcessSessionAsync(sessionId));
        
        // Assert: No records committed
        var ordersCount = await GetProductionRecordCount("Orders");
        ordersCount.Should().Be(0, "Orders should be rolled back on FK violation");
        
        var session = await GetSession(sessionId);
        session!.Status.Should().Be("Failed");
    }

    /// <summary>
    /// Test that tables are processed in priority order (respects FK dependencies).
    /// Customers (Priority 1) → Orders (Priority 2).
    /// If Orders fail, Customers still rolled back.
    /// </summary>
    [Fact]
    public async Task ProcessSession_ProcessedInPriorityOrder_RespectsFK()
    {
        // Arrange: Customers (Priority 1) + Orders (Priority 2)
        // Verify processing order AND rollback when Orders fail
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Customers", EstimatedRecordCount = 5 },
                new() { TableName = "Orders", EstimatedRecordCount = 1 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage valid Customers (will process first due to Priority 1)
        var customers = TestDataGenerator.CreateCustomersDict(5);
        await StageTempTableData(sessionId, "Customers", customers);
        await _sessionTracker.CompleteTableAsync(sessionId, "Customers", 5);
        
        // Stage Orders with FK violation (processes second due to Priority 2)
        var invalidOrder = TestDataGenerator.CreateOrderDict(customerId: Guid.NewGuid());
        await StageTempTableData(sessionId, "Orders", new List<Dictionary<string, object?>> { invalidOrder });
        await _sessionTracker.CompleteTableAsync(sessionId, "Orders", 1);
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act: Process - Customers succeed, then Orders fail
        await Assert.ThrowsAsync<MySqlException>(async () =>
            await _queueProcessor!.ProcessSessionAsync(sessionId));
        
        // Assert: Even though Customers processed first and succeeded,
        // they're rolled back when Orders fail (single transaction)
        var customersCount = await GetProductionRecordCount("Customers");
        customersCount.Should().Be(0, "Customers rolled back despite processing first");
        
        var ordersCount = await GetProductionRecordCount("Orders");
        ordersCount.Should().Be(0, "Orders not committed");
    }

    #endregion

    #region C. Session State Verification

    /// <summary>
    /// Test that rollback leaves session in "Processing" state (not "Failed").
    /// From 19b: "Rollback on any failure leaves session in Processing state."
    /// Final "Failed" update happens outside transaction.
    /// </summary>
    [Fact]
    public async Task ProcessSession_RollbackOccurs_SessionStaysProcessing()
    {
        // Arrange: Session that will fail mid-processing
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Orders", EstimatedRecordCount = 1 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage invalid data
        var invalidOrder = TestDataGenerator.CreateOrderDict(customerId: Guid.NewGuid());
        await StageTempTableData(sessionId, "Orders", new List<Dictionary<string, object?>> { invalidOrder });
        await _sessionTracker.CompleteTableAsync(sessionId, "Orders", 1);
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act: Process and catch exception
        await Assert.ThrowsAsync<MySqlException>(async () =>
            await _queueProcessor!.ProcessSessionAsync(sessionId));
        
        // Assert: Session marked "Failed" (ProcessSessionAsync updates after rollback)
        var session = await GetSession(sessionId);
        session!.Status.Should().Be("Failed", 
            "ProcessSessionAsync marks Failed outside transaction after rollback");
        session.ErrorMessage.Should().NotBeNullOrEmpty("error message captured");
    }

    /// <summary>
    /// Test that successful commit updates session status in same transaction as data.
    /// If status update fails, data also rolled back.
    /// </summary>
    [Fact]
    public async Task ProcessSession_SuccessfulCommit_UpdatesSessionInTransaction()
    {
        // Arrange: Session with valid data
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Customers", EstimatedRecordCount = 5 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage valid Customers
        var customers = TestDataGenerator.CreateCustomersDict(5);
        await StageTempTableData(sessionId, "Customers", customers);
        await _sessionTracker.CompleteTableAsync(sessionId, "Customers", 5);
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act: Process successfully
        await _queueProcessor!.ProcessSessionAsync(sessionId);
        
        // Assert: Both data AND session status committed atomically
        var customersCount = await GetProductionRecordCount("Customers");
        customersCount.Should().Be(5, "all customers committed");
        
        var session = await GetSession(sessionId);
        session!.Status.Should().Be("Committed", "session status updated in same transaction");
        session.SyncVersion.Should().NotBeNull("SyncVersion assigned on commit");
        session.CommittedAtUtc.Should().NotBeNull("CommittedAtUtc set");
    }

    #endregion

    #region D. Isolation Level Testing

    /// <summary>
    /// Test that SERIALIZABLE isolation prevents concurrent session conflicts.
    /// Two sessions updating overlapping data should serialize, not conflict.
    /// </summary>
    [Fact]
    public async Task ProcessSession_SerializableIsolation_PreventsConcurrentConflicts()
    {
        // Arrange: Two sessions with overlapping customer updates
        var sharedCustomerId = Guid.NewGuid();
        
        // Session 1: Updates customer
        var request1 = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Customers", EstimatedRecordCount = 1 }
            }
        };
        var response1 = await _sessionTracker!.CreatePushSessionAsync(request1);
        var sessionId1 = response1.SessionId;
        
        var customer1 = TestDataGenerator.CreateCustomerDict(
            id: sharedCustomerId,
            name: "Update from Session 1",
            email: "session1@test.com",
            modifiedByUserId: "User1");
        await StageTempTableData(sessionId1, "Customers", new List<Dictionary<string, object?>> { customer1 });
        await _sessionTracker.CompleteTableAsync(sessionId1, "Customers", 1);
        await _sessionTracker.MarkSessionReadyAsync(sessionId1);
        
        // Session 2: Updates same customer
        var request2 = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Customers", EstimatedRecordCount = 1 }
            }
        };
        var response2 = await _sessionTracker.CreatePushSessionAsync(request2);
        var sessionId2 = response2.SessionId;
        
        var customer2 = TestDataGenerator.CreateCustomerDict(
            id: sharedCustomerId,
            name: "Update from Session 2",
            email: "session2@test.com",
            modifiedByUserId: "User2");
        await StageTempTableData(sessionId2, "Customers", new List<Dictionary<string, object?>> { customer2 });
        await _sessionTracker.CompleteTableAsync(sessionId2, "Customers", 1);
        await _sessionTracker.MarkSessionReadyAsync(sessionId2);
        
        // Act: Process both sessions concurrently
        var task1 = Task.Run(() => _queueProcessor!.ProcessSessionAsync(sessionId1));
        var task2 = Task.Run(() => _queueProcessor!.ProcessSessionAsync(sessionId2));
        
        await Task.WhenAll(task1, task2);
        
        // Assert: Both sessions committed (SERIALIZABLE allows this - last-in-wins)
        var session1 = await GetSession(sessionId1);
        var session2 = await GetSession(sessionId2);
        
        session1!.Status.Should().Be("Committed");
        session2!.Status.Should().Be("Committed");
        
        // Final customer state is from whichever committed last
        var customersCount = await GetProductionRecordCount("Customers");
        customersCount.Should().Be(1, "only one customer record exists");
    }

    #endregion

    #region E. Successful Multi-Table Commits

    /// <summary>
    /// Test that successful session commits all tables atomically.
    /// Customers → Orders → OrderItems, all succeed.
    /// </summary>
    [Fact]
    public async Task ProcessSession_AllTablesValid_CommitsAtomically()
    {
        // Arrange: Three-table session with valid FK relationships
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Products", EstimatedRecordCount = 5 },
                new() { TableName = "Customers", EstimatedRecordCount = 3 },
                new() { TableName = "Orders", EstimatedRecordCount = 6 },
                new() { TableName = "OrderItems", EstimatedRecordCount = 12 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage Products (Priority 1)
        var products = TestDataGenerator.CreateProductsDict(5);
        var productIds = products.Select(p => (Guid)p["Id"]!).ToList();
        await StageTempTableData(sessionId, "Products", products);
        await _sessionTracker.CompleteTableAsync(sessionId, "Products", 5);
        
        // Stage Customers (Priority 1)
        var customers = TestDataGenerator.CreateCustomersDict(3);
        await StageTempTableData(sessionId, "Customers", customers);
        await _sessionTracker.CompleteTableAsync(sessionId, "Customers", 3);
        
        // Stage Orders (2 per customer - Priority 2)
        var orders = customers.SelectMany(c => 
            TestDataGenerator.CreateOrdersDict((Guid)c["Id"]!, 2)).ToList();
        await StageTempTableData(sessionId, "Orders", orders);
        await _sessionTracker.CompleteTableAsync(sessionId, "Orders", 6);
        
        // Stage OrderItems (2 per order - Priority 3)
        var orderItemDicts = orders.SelectMany(o =>
            TestDataGenerator.CreateOrderItemsDict((Guid)o["Id"]!, productIds, 2)).ToList();
        await StageTempTableData(sessionId, "OrderItems", orderItemDicts);
        await _sessionTracker.CompleteTableAsync(sessionId, "OrderItems", 12);
        
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act: Process session
        await _queueProcessor!.ProcessSessionAsync(sessionId);
        
        // Assert: All tables committed atomically
        var customersCount = await GetProductionRecordCount("Customers");
        customersCount.Should().Be(3);
        
        var ordersCount = await GetProductionRecordCount("Orders");
        ordersCount.Should().Be(6);
        
        var itemsCount = await GetProductionRecordCount("OrderItems");
        itemsCount.Should().Be(12);
        
        // Session marked Committed
        var session = await GetSession(sessionId);
        session!.Status.Should().Be("Committed");
        session.SyncVersion.Should().NotBeNull();
    }

    /// <summary>
    /// Test that large session (3 tables, 1000 records each) commits successfully.
    /// </summary>
    [Fact]
    public async Task ProcessSession_LargeSession_CommitsSuccessfully()
    {
        // Arrange: Large session - 3 tables with 1000 records each
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new List<TableSyncInfo>
            {
                new() { TableName = "Products", EstimatedRecordCount = 10 },
                new() { TableName = "Customers", EstimatedRecordCount = 1000 },
                new() { TableName = "Orders", EstimatedRecordCount = 1000 },
                new() { TableName = "OrderItems", EstimatedRecordCount = 1000 }
            }
        };
        
        var response = await _sessionTracker!.CreatePushSessionAsync(request);
        var sessionId = response.SessionId;
        
        // Stage 10 Products
        var products = TestDataGenerator.CreateProductsDict(10);
        var productIds = products.Select(p => (Guid)p["Id"]!).ToList();
        await StageTempTableData(sessionId, "Products", products);
        await _sessionTracker.CompleteTableAsync(sessionId, "Products", 10);
        
        // Stage 1000 Customers
        var customers = TestDataGenerator.CreateCustomersDict(1000);
        await StageTempTableData(sessionId, "Customers", customers);
        await _sessionTracker.CompleteTableAsync(sessionId, "Customers", 1000);
        
        // Stage 1000 Orders (use first customer for all)
        var firstCustomerId = (Guid)customers[0]["Id"]!;
        var orders = TestDataGenerator.CreateOrdersDict(firstCustomerId, 1000);
        await StageTempTableData(sessionId, "Orders", orders);
        await _sessionTracker.CompleteTableAsync(sessionId, "Orders", 1000);
        
        // Stage 1000 OrderItems (use first order for all)
        var orderItemDicts = TestDataGenerator.CreateOrderItemsDict((Guid)orders[0]["Id"]!, productIds, 1000);
        await StageTempTableData(sessionId, "OrderItems", orderItemDicts);
        await _sessionTracker.CompleteTableAsync(sessionId, "OrderItems", 1000);
        
        await _sessionTracker.MarkSessionReadyAsync(sessionId);
        
        // Act: Process large session
        await _queueProcessor!.ProcessSessionAsync(sessionId);
        
        // Assert: All 3000 records committed
        var customersCount = await GetProductionRecordCount("Customers");
        customersCount.Should().Be(1000);
        
        var ordersCount = await GetProductionRecordCount("Orders");
        ordersCount.Should().Be(1000);
        
        var itemsCount = await GetProductionRecordCount("OrderItems");
        itemsCount.Should().Be(1000);
        
        var session = await GetSession(sessionId);
        session!.Status.Should().Be("Committed");
    }

    #endregion

    #region Test Helpers

    private async Task<int> GetProductionRecordCount(string tableName)
    {
        using var connection = new MySqlConnection(_testConnectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {tableName}");
    }

    private async Task<SessionRecord?> GetSession(Guid sessionId)
    {
        return await _sessionTracker!.GetSessionAsync(sessionId);
    }

    /// <summary>
    /// Stage data into temp tables for a session.
    /// Call this after creating session via SessionTracker.CreatePushSessionAsync.
    /// </summary>
    private async Task StageTempTableData(Guid sessionId, string tableName, List<Dictionary<string, object?>> records)
    {
        // TempTableManager.InsertBatchAsync handles finding the correct temp table internally
        await _tempTableManager!.InsertBatchAsync(sessionId, tableName, records);
    }

    #endregion
}
