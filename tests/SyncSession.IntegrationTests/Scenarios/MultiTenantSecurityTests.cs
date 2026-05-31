using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Samples.Shared.TestData;
using SyncSession.Server.Database;
using SyncSession.Server.Models;
using SyncSession.Server.Services;
using MySqlConnector;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// Session 21b: Multi-tenant security isolation tests
/// Validates that pull operations do not leak records across tenants.
/// </summary>
[Collection("MariaDB Collection")]
public class MultiTenantSecurityTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private readonly ServerSyncConfiguration _config;
    private string _testConnectionString = string.Empty;

    public MultiTenantSecurityTests(MariaDbFixture fixture)
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
        _testConnectionString = await _fixture.CreateTestDatabaseAsync(nameof(MultiTenantSecurityTests));
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
    }

    /// <summary>
    /// Helper: Push a customer record for a specific tenant through the full push flow.
    /// Returns the committed push session ID.
    /// </summary>
    private async Task<Guid> PushCustomerForTenant(
        MySqlServerDatabase serverDb,
        TempTableManager tempTableManager,
        SessionTracker sessionTracker,
        SyncQueueProcessor queueProcessor,
        Guid clientId,
        Guid tenantId,
        string customerName,
        string email,
        string modifiedByUserId)
    {
        var pushRequest = new PushSessionBeginRequest
        {
            DeviceId = clientId,
            Tables = new List<TableSyncInfo>
            {
                new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 }
            }
        };
        var pushResponse = await sessionTracker.CreatePushSessionAsync(pushRequest);
        var pushSessionId = pushResponse.SessionId;

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = customerName,
            Email = email,
            ModifiedByUserId = modifiedByUserId,
            IsDirty = false,
            IsDeleted = false
        };

        await tempTableManager.InsertBatchAsync(
            pushSessionId,
            "Customers",
            new[] { EntityReflectionHelper.EntityToDictionary(customer) }
        );

        await sessionTracker.CompleteTableAsync(pushSessionId, "Customers", 1);
        await sessionTracker.MarkSessionReadyAsync(pushSessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        var session = await serverDb.GetSessionAsync(pushSessionId);
        session!.Status.Should().Be("Committed");

        return pushSessionId;
    }

    /// <summary>
    /// CRITICAL BUG TEST: Pull should not return records from other tenants.
    /// Tenant A pushes a customer, Tenant B pulls — Tenant B should receive 0 records.
    /// This test proves the multi-tenant pull leak bug.
    /// </summary>
    [Fact]
    public async Task PullAsync_ShouldNotReturnOtherTenantRecords()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientAId = Guid.NewGuid();
        var clientBId = Guid.NewGuid();
        var deviceBId = Guid.NewGuid();

        // Step 1: Tenant A pushes a customer
        var pushSessionId = await PushCustomerForTenant(
            serverDb, tempTableManager, sessionTracker, queueProcessor,
            clientAId, tenantA,
            "Tenant A Customer", "a@tenantA.com", "UserA");

        // Step 2: Tenant B pulls — tenant context via request
        var pullRequest = new PullSessionBeginRequest
        {
            DeviceId = deviceBId,
            TenantId = tenantB,
            TableNames = new[] { "Customers" }
        };
        var pullResponse = await sessionTracker.CreatePullSessionAsync(pullRequest);

        // Assert: Tenant B should see 0 Customers records
        pullResponse.Success.Should().BeTrue();

        if (pullResponse.Tables.ContainsKey("Customers"))
        {
            // If the table key exists, it should have 0 records
            pullResponse.Tables["Customers"].TotalRecords.Should().Be(0,
                "Tenant B must NOT receive Tenant A's customer records");
        }
        // If "Customers" key is absent entirely, that's also correct (no data to pull)
    }

    /// <summary>
    /// CRITICAL BUG TEST: Each tenant should only receive their own records on pull.
    /// Both tenants push, both pull — each should only get their own data.
    /// </summary>
    [Fact]
    public async Task PullAsync_ShouldOnlyReturnOwnTenantRecords()
    {
        // Arrange
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_testConnectionString, tableMetaDataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, _config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientAId = Guid.NewGuid();
        var clientBId = Guid.NewGuid();
        var deviceAId = Guid.NewGuid();
        var deviceBId = Guid.NewGuid();

        // Step 1: Both tenants push one customer each (no tenant context needed for push)
        var pushSessionA = await PushCustomerForTenant(
            serverDb, tempTableManager, sessionTracker, queueProcessor,
            clientAId, tenantA,
            "Customer from Tenant A", "a@tenantA.com", "UserA");

        var pushSessionB = await PushCustomerForTenant(
            serverDb, tempTableManager, sessionTracker, queueProcessor,
            clientBId, tenantB,
            "Customer from Tenant B", "b@tenantB.com", "UserB");

        // Verify both records exist in main table
        using (var conn = new MySqlConnection(_testConnectionString))
        {
            await conn.OpenAsync();
            var totalCustomers = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Customers");
            totalCustomers.Should().Be(2, "both tenants pushed 1 customer each");
        }

        // Step 2: Tenant A pulls — should only get their own record
        var pullRequestA = new PullSessionBeginRequest
        {
            DeviceId = deviceAId,
            TenantId = tenantA,
            TableNames = new[] { "Customers" }
        };
        var pullResponseA = await sessionTracker.CreatePullSessionAsync(pullRequestA);

        pullResponseA.Success.Should().BeTrue();
        pullResponseA.Tables.Should().ContainKey("Customers");
        pullResponseA.Tables["Customers"].TotalRecords.Should().Be(1,
            "Tenant A should only see 1 record (their own)");

        var pullSessionAId = pullResponseA.PullSessionId;
        var batchA = await tempTableManager.GetPullBatchAsync(
            pullSessionAId, "Customers", offset: 0, limit: 1000);

        batchA.Records.Should().HaveCount(1);
        batchA.Records.First()["Name"].ToString().Should().Be("Customer from Tenant A");
        batchA.Records.First()["TenantId"].ToString().Should().Be(tenantA.ToString());

        // Cleanup pull A
        await serverDb.MarkSessionsProcessedAsync(deviceAId, new[] { pushSessionA, pushSessionB });
        await tempTableManager.CleanupPullSessionAsync(pullSessionAId, pullResponseA.Tables.Values);

        // Step 3: Tenant B pulls — should only get their own record
        var pullRequestB = new PullSessionBeginRequest
        {
            DeviceId = deviceBId,
            TenantId = tenantB,
            TableNames = new[] { "Customers" }
        };
        var pullResponseB = await sessionTracker.CreatePullSessionAsync(pullRequestB);

        pullResponseB.Success.Should().BeTrue();
        pullResponseB.Tables.Should().ContainKey("Customers");
        pullResponseB.Tables["Customers"].TotalRecords.Should().Be(1,
            "Tenant B should only see 1 record (their own)");

        var pullSessionBId = pullResponseB.PullSessionId;
        var batchB = await tempTableManager.GetPullBatchAsync(
            pullSessionBId, "Customers", offset: 0, limit: 1000);

        batchB.Records.Should().HaveCount(1);
        batchB.Records.First()["Name"].ToString().Should().Be("Customer from Tenant B");
        batchB.Records.First()["TenantId"].ToString().Should().Be(tenantB.ToString());

        // Cleanup pull B
        await serverDb.MarkSessionsProcessedAsync(deviceBId, new[] { pushSessionA, pushSessionB });
        await tempTableManager.CleanupPullSessionAsync(pullSessionBId, pullResponseB.Tables.Values);
    }
}
