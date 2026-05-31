using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Core.DTOs;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Exceptions;
using SyncSession.Core.Models;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.Services;

/// <summary>
/// Service layer error handling tests - validates proper exception handling
/// Session 16e: Comprehensive error scenarios for SessionTracker, TempTableManager, SyncQueueProcessor
/// </summary>
[Collection("MariaDB Collection")]
public class ServiceLayerErrorHandlingTests
{
    private readonly MariaDbFixture _fixture;
    private readonly TestDatabaseFactory _dbFactory;

    public ServiceLayerErrorHandlingTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    #region SessionTracker - CreatePushSession Error Tests

    [Fact]
    public async Task SessionTracker_CreatePushSession_NullRequest_ThrowsArgumentNullException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_CreatePushSession_NullRequest_ThrowsArgumentNullException));

        
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await sessionTracker.CreatePushSessionAsync(null!));
    }

    [Fact]
    public async Task SessionTracker_CreatePushSession_EmptyDeviceId_ThrowsArgumentException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_CreatePushSession_EmptyDeviceId_ThrowsArgumentException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.Empty,
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config,
                estimatedCounts: new Dictionary<string, int> { ["Customers"] = 100 })
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sessionTracker.CreatePushSessionAsync(request));
    }

    [Fact]
    public async Task SessionTracker_CreatePushSession_NoTables_ThrowsArgumentException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_CreatePushSession_NoTables_ThrowsArgumentException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = Array.Empty<TableSyncInfo>()
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sessionTracker.CreatePushSessionAsync(request));
    }

    [Fact]
    public async Task SessionTracker_CreatePushSession_NullTableName_ThrowsArgumentException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_CreatePushSession_NullTableName_ThrowsArgumentException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        
        var request = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new[] 
            { 
                new TableSyncInfo 
                { 
                    TableName = null!, 
                    EstimatedRecordCount = 100
                } 
            }
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sessionTracker.CreatePushSessionAsync(request));
    }

    #endregion

    #region SessionTracker - CreatePullSession Error Tests

    [Fact]
    public async Task SessionTracker_CreatePullSession_NullRequest_ThrowsArgumentNullException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_CreatePullSession_NullRequest_ThrowsArgumentNullException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await sessionTracker.CreatePullSessionAsync(null!));
    }

    #endregion

    #region SessionTracker - CompleteTable Error Tests

    [Fact]
    public async Task SessionTracker_CompleteTable_NonExistentSession_ReturnsFailure()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_CompleteTable_NonExistentSession_ReturnsFailure));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        var nonExistentSessionId = Guid.NewGuid();

        // Act
        var result = await sessionTracker.CompleteTableAsync(nonExistentSessionId, "Customers", 100);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task SessionTracker_CompleteTable_NullTableName_ThrowsException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_CompleteTable_NullTableName_ThrowsException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sessionTracker.CompleteTableAsync(Guid.NewGuid(), null!, 100));
    }

    #endregion

    #region SessionTracker - MarkSessionReady Error Tests

    [Fact]
    public async Task SessionTracker_MarkSessionReady_NonExistentSession_ReturnsFalse()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_MarkSessionReady_NonExistentSession_ReturnsFalse));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        var nonExistentSessionId = Guid.NewGuid();

        // Act
        var result = await sessionTracker.MarkSessionReadyAsync(nonExistentSessionId);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region SessionTracker - GetSessionStatus Tests

    [Fact]
    public async Task SessionTracker_GetSessionStatus_NonExistentSession_ReturnsNull()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionTracker_GetSessionStatus_NonExistentSession_ReturnsNull));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);

        var nonExistentSessionId = Guid.NewGuid();

        // Act
        var result = await sessionTracker.GetSessionStatusAsync(nonExistentSessionId);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region TempTableManager - GetTempTable Error Tests

    [Fact]
    public async Task TempTableManager_GetTempTable_NullTableName_ThrowsArgumentNullException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTableManager_GetTempTable_NullTableName_ThrowsArgumentNullException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await tempTableManager.GetTempTableForPushAsync(Guid.NewGuid(), null!, 100));
    }

    [Fact]
    public async Task TempTableManager_GetTempTable_EmptySessionId_ThrowsArgumentException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTableManager_GetTempTable_EmptySessionId_ThrowsArgumentException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await tempTableManager.GetTempTableForPushAsync(Guid.Empty, "Customers", 100));
    }

    [Fact]
    public async Task TempTableManager_GetTempTable_NegativeEstimatedRecordCount_ThrowsArgumentException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTableManager_GetTempTable_NegativeEstimatedRecordCount_ThrowsArgumentException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await tempTableManager.GetTempTableForPushAsync(Guid.NewGuid(), "Customers", -1));
    }

    #endregion

    #region TempTableManager - InsertBatch Error Tests

    [Fact]
    public async Task TempTableManager_InsertBatch_EmptyRecordsList_ReturnsZero()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTableManager_InsertBatch_EmptyRecordsList_ReturnsZero));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        var sessionId = Guid.NewGuid();
        var emptyRecords = new List<Dictionary<string, object?>>();

        // Act
        var result = await tempTableManager.InsertBatchAsync(sessionId, "Customers", emptyRecords);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public async Task TempTableManager_InsertBatch_NonExistentSession_ThrowsInvalidOperationException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTableManager_InsertBatch_NonExistentSession_ThrowsInvalidOperationException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        var nonExistentSessionId = Guid.NewGuid();
        var records = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = Guid.NewGuid().ToString(), ["Name"] = "Test" }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await tempTableManager.InsertBatchAsync(nonExistentSessionId, "Customers", records));

        exception.Message.Should().Contain("Temp table not found");
    }

    #endregion

    #region TempTableManager - Cleanup Error Tests

    [Fact]
    public async Task TempTableManager_CleanupSessionTables_NonExistentSession_HandlesGracefully()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTableManager_CleanupSessionTables_NonExistentSession_HandlesGracefully));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        var nonExistentSessionId = Guid.NewGuid();

        // Act - Should not throw, just handle gracefully
        await tempTableManager.CleanupSessionTablesAsync(nonExistentSessionId);

        // Assert - No exception means success
    }

    [Fact]
    public async Task TempTableManager_CleanupPullSession_NonExistentSession_HandlesGracefully()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(TempTableManager_CleanupPullSession_NonExistentSession_HandlesGracefully));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        var nonExistentPullSessionId = Guid.NewGuid();
        
        // Create test metadata
        var tables = new List<SyncSessionTableMetadata>
        {
            new SyncSessionTableMetadata
            {
                TableName = "Customers",
                TempTableName = "TempPullCustomers",
                UsesSharedTable = true,
                TotalRecords = 0
            }
        };

        // Act - Should not throw, just handle gracefully
        await tempTableManager.CleanupPullSessionAsync(nonExistentPullSessionId, tables);

        // Assert - No exception means success
    }

    #endregion

    #region SyncQueueProcessor Error Tests

    [Fact]
    public async Task SyncQueueProcessor_ProcessSession_NonExistentSession_ThrowsSyncException()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SyncQueueProcessor_ProcessSession_NonExistentSession_ThrowsSyncException));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var processor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var nonExistentSessionId = Guid.NewGuid();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SyncException>(async () =>
            await processor.ProcessSessionAsync(nonExistentSessionId));

        exception.Message.Should().Contain("not found");
    }

    #endregion
}
