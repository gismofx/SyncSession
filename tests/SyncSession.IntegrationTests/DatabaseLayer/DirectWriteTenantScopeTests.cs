using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// A direct write (server-side write through <see cref="DirectWriteService"/>) must be owned by the
/// tenant whose rows it wrote, so pull-begin's tenant scope keeps it away from other tenants.
///
/// Before this fix the Direct session was created with no TenantId. Pull-begin keeps sessions where
/// <c>TenantId = @TenantId OR TenantId IS NULL</c>, so every direct write landed in every tenant's
/// devices' unseen list - where, yielding them no rows, it is never acknowledged and stays in every
/// pull's IN list: the same growth that timed pull/begin out on production on 2026-10-11 (see
/// <see cref="PullTenantScopeTests"/>). Any server-side app writing tenant data (e.g. a back-office
/// app) would rebuild that list one write at a time.
///
/// A write that touches a shared (non-tenant) table, or rows of more than one tenant, has no single
/// owner and must stay untenanted so it still reaches everyone - the guard tests hold that line.
/// </summary>
[Collection("MariaDB Collection")]
public class DirectWriteTenantScopeTests
{
    private readonly TestDatabaseFactory _dbFactory;

    public DirectWriteTenantScopeTests(MariaDbFixture fixture)
    {
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    [Fact]
    public async Task WriteAsync_ForOneTenant_IsNotOfferedToAnotherTenant()
    {
        var (db, writer) = await BuildAsync(nameof(WriteAsync_ForOneTenant_IsNotOfferedToAnotherTenant));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var result = await writer.WriteAsync(NewCustomer(tenantA), "office-user", tenantA.ToString());

        var unseenForB = (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), tenantB)).ToList();
        unseenForB.Should().NotContain(result.SessionId,
            "a direct write of tenant A's row can never yield a tenant B device a row");
    }

    [Fact]
    public async Task WriteAsync_ForOneTenant_IsOfferedToThatTenant()
    {
        // Control: the fix must not hide a direct write from the tenant it belongs to.
        var (db, writer) = await BuildAsync(nameof(WriteAsync_ForOneTenant_IsOfferedToThatTenant));
        var tenantA = Guid.NewGuid();

        var result = await writer.WriteAsync(NewCustomer(tenantA), "office-user", tenantA.ToString());

        var unseenForA = (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), tenantA)).ToList();
        unseenForA.Should().Contain(result.SessionId);
    }

    [Fact]
    public async Task WriteAsync_WithoutTenantArgument_TakesTheTenantFromTheEntity()
    {
        // The tenantId argument is optional; the row's own TenantId is what a pull filters on.
        var (db, writer) = await BuildAsync(nameof(WriteAsync_WithoutTenantArgument_TakesTheTenantFromTheEntity));
        var tenantA = Guid.NewGuid();

        var result = await writer.WriteAsync(NewCustomer(tenantA), "office-user");

        (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), Guid.NewGuid()))
            .Should().NotContain(result.SessionId);
        (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), tenantA))
            .Should().Contain(result.SessionId);
    }

    [Fact]
    public async Task WriteBatchAsync_OneTenant_IsNotOfferedToAnotherTenant()
    {
        var (db, writer) = await BuildAsync(nameof(WriteBatchAsync_OneTenant_IsNotOfferedToAnotherTenant));
        var tenantA = Guid.NewGuid();
        var customer = NewCustomer(tenantA);

        var result = await writer.WriteBatchAsync(new Dictionary<string, List<object>>
        {
            ["Customers"] = new() { customer },
            ["Orders"] = new() { NewOrder(customer) }
        }, "office-user", tenantA.ToString());

        (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), Guid.NewGuid()))
            .Should().NotContain(result.SessionId);
        (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), tenantA))
            .Should().Contain(result.SessionId);
    }

    [Fact]
    public async Task DeleteAsync_ForOneTenant_IsNotOfferedToAnotherTenant()
    {
        var (db, writer) = await BuildAsync(nameof(DeleteAsync_ForOneTenant_IsNotOfferedToAnotherTenant));
        var tenantA = Guid.NewGuid();
        var customer = NewCustomer(tenantA);
        await writer.WriteAsync(customer, "office-user", tenantA.ToString());

        var deleted = await writer.DeleteAsync<Customer>(customer.Id, "office-user", tenantA.ToString());
        var deletedByName = await writer.DeleteAsync("Customers", customer.Id, "office-user", tenantA.ToString());

        var unseenForB = (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), Guid.NewGuid())).ToList();
        unseenForB.Should().NotContain(new[] { deleted.SessionId, deletedByName.SessionId });
        var unseenForA = (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), tenantA)).ToList();
        unseenForA.Should().Contain(new[] { deleted.SessionId, deletedByName.SessionId });
    }

    [Fact]
    public async Task WriteBatchAsync_TouchingASharedTable_StaysOfferedToEveryTenant()
    {
        // Guard: Products is shared (ISyncEntity, no TenantId). Its rows reach every tenant, so a
        // session that wrote one must not be claimed by a single tenant, even alongside tenant rows.
        var (db, writer) = await BuildAsync(nameof(WriteBatchAsync_TouchingASharedTable_StaysOfferedToEveryTenant));
        var tenantA = Guid.NewGuid();

        var result = await writer.WriteBatchAsync(new Dictionary<string, List<object>>
        {
            ["Customers"] = new() { NewCustomer(tenantA) },
            ["Products"] = new() { NewProduct() }
        }, "office-user");

        (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), Guid.NewGuid()))
            .Should().Contain(result.SessionId);
    }

    [Fact]
    public async Task WriteBatchAsync_RowsOfTwoTenants_StaysOfferedToBoth()
    {
        // Guard: a session holding two tenants' rows has no single owner; tagging it with either
        // would hide the other tenant's rows from its devices.
        var (db, writer) = await BuildAsync(nameof(WriteBatchAsync_RowsOfTwoTenants_StaysOfferedToBoth));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var result = await writer.WriteBatchAsync(new Dictionary<string, List<object>>
        {
            ["Customers"] = new() { NewCustomer(tenantA), NewCustomer(tenantB) }
        }, "system");

        (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), tenantA)).Should().Contain(result.SessionId);
        (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), tenantB)).Should().Contain(result.SessionId);
    }

    // -- helpers --------------------------------------------------------------

    private async Task<(MySqlServerDatabase Db, DirectWriteService Writer)> BuildAsync(string testName)
    {
        var connectionString = await _dbFactory.CreateDatabaseAsync(testName);
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        // Products is the schema's shared table with no foreign keys; registered here only, so the
        // shared-table guard has something to write without touching the default configuration.
        config.RegisterTable<Product>(priority: 4, enabled: true);
        var cache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var db = new MySqlServerDatabase(connectionString, cache, config, NullLogger<MySqlServerDatabase>.Instance);
        var writer = new DirectWriteService(db, cache, new DefaultDirectWriteTenantValidator(),
            NullLogger<DirectWriteService>.Instance);
        return (db, writer);
    }

    private static Customer NewCustomer(Guid tenantId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Direct Write Co",
        Email = "dw@test.com",
        TenantId = tenantId
    };

    private static Order NewOrder(Customer customer) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customer.Id,
        OrderNumber = "DW-1",
        OrderDate = DateTime.UtcNow,
        TenantId = customer.TenantId
    };

    private static Product NewProduct() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Widget",
        SKU = "W-" + Guid.NewGuid().ToString("N")[..8],
        Price = 1m
    };
}
