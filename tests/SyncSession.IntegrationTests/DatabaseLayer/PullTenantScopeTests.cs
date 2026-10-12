using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.Models;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// A device should only be asked about sync sessions from its own tenant.
///
/// FindUnseenSessionIdsAsync had no tenant filter, so every committed session from every tenant
/// landed in the pull's IN list and stayed there forever: a session from another tenant can never
/// yield a row for this device (the per-table snapshot filters rows by TenantId), so it is never
/// acknowledged. On production (2026-10-11) 626 of one device's 629 unseen sessions belonged to
/// other tenants; that list pushed the snapshot's plan to a full scan of 1.3M rows and
/// pull/begin failed at the 30 s command timeout.
///
/// The filter is only equivalent to the old behaviour when every requested table is tenant-scoped.
/// A shared (non-tenant) table's rows reach every tenant, so a session from another tenant that
/// touched it must still be offered — the guard test below holds that line.
/// </summary>
[Collection("MariaDB Collection")]
public class PullTenantScopeTests
{
    private readonly TestDatabaseFactory _dbFactory;

    public PullTenantScopeTests(MariaDbFixture fixture)
    {
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    [Fact]
    public async Task FindUnseen_WithTenant_ExcludesOtherTenants_KeepsOwnAndUntenanted()
    {
        var (db, _) = await BuildAsync(nameof(FindUnseen_WithTenant_ExcludesOtherTenants_KeepsOwnAndUntenanted));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var own = await CommittedSessionAsync(db, tenantA);
        var other = await CommittedSessionAsync(db, tenantB);
        var untenanted = await CommittedSessionAsync(db, null);

        var unseen = (await db.FindUnseenSessionIdsAsync(Guid.NewGuid(), tenantA)).ToList();

        unseen.Should().Contain(own);
        unseen.Should().Contain(untenanted, "a session with no tenant is not known to belong elsewhere");
        unseen.Should().NotContain(other, "another tenant's session can never yield a row for this device");
    }

    [Fact]
    public async Task FindUnseen_WithoutTenant_StillReturnsEverySession()
    {
        // Single-tenant deployments pass no tenant and must see exactly what they saw before.
        var (db, _) = await BuildAsync(nameof(FindUnseen_WithoutTenant_StillReturnsEverySession));

        var a = await CommittedSessionAsync(db, Guid.NewGuid());
        var b = await CommittedSessionAsync(db, null);

        var unseen = (await db.FindUnseenSessionIdsAsync(Guid.NewGuid())).ToList();

        unseen.Should().Contain(new[] { a, b });
    }

    [Fact]
    public async Task PullBegin_OnlyOtherTenantSessions_ReturnsNoTables()
    {
        var (db, tracker) = await BuildAsync(nameof(PullBegin_OnlyOtherTenantSessions_ReturnsNoTables));
        await CommittedSessionAsync(db, Guid.NewGuid());

        var pull = await tracker.CreatePullSessionAsync(new PullSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            TableNames = new[] { "Customers" }
        });

        pull.Tables.Should().BeEmpty(
            "every requested table is tenant-scoped, so another tenant's session has nothing for this device");
    }

    [Fact]
    public async Task PullBegin_OwnTenantSession_IsStillOffered()
    {
        var (db, tracker) = await BuildAsync(nameof(PullBegin_OwnTenantSession_IsStillOffered));
        var tenant = Guid.NewGuid();
        await CommittedSessionAsync(db, tenant);

        var pull = await tracker.CreatePullSessionAsync(new PullSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            TenantId = tenant,
            TableNames = new[] { "Customers" }
        });

        pull.Tables.Should().ContainKey("Customers");
    }

    [Fact]
    public async Task PullBegin_SharedTableRequested_StillOffersOtherTenantSessions()
    {
        // OrderItems has no TenantId (ISyncEntity, not IMultiTenantSyncEntity): its rows go to every
        // tenant. Filtering sessions by tenant here would silently stop a shared change made by one
        // tenant from reaching the others.
        var (db, tracker) = await BuildAsync(nameof(PullBegin_SharedTableRequested_StillOffersOtherTenantSessions));
        await CommittedSessionAsync(db, Guid.NewGuid());

        var pull = await tracker.CreatePullSessionAsync(new PullSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            TableNames = new[] { "Customers", "OrderItems" }
        });

        pull.Tables.Should().ContainKey("OrderItems");
    }

    // -- helpers --------------------------------------------------------------

    /// <summary>
    /// Creates a fresh database for the test and the server stack pull-begin runs through. The
    /// tracker is given the metadata cache, as DI does, so the tenant scoping is actually active.
    /// </summary>
    private async Task<(MySqlServerDatabase Db, SessionTracker Tracker)> BuildAsync(string testName)
    {
        var connectionString = await _dbFactory.CreateDatabaseAsync(testName);
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var cache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var db = new MySqlServerDatabase(connectionString, cache, config, NullLogger<MySqlServerDatabase>.Instance);
        var temp = new TempTableManager(db, config, NullLogger<TempTableManager>.Instance);
        var tracker = new SessionTracker(db, temp, NullLogger<SessionTracker>.Instance, cache);
        return (db, tracker);
    }

    /// <summary>
    /// Records a committed push session owned by <paramref name="tenantId"/> (or by no tenant when
    /// null), without staging any rows: these tests are about which sessions a device is offered,
    /// not what the sessions contain.
    /// </summary>
    private static async Task<Guid> CommittedSessionAsync(MySqlServerDatabase db, Guid? tenantId)
    {
        var session = new SessionRecord
        {
            SessionId = Guid.NewGuid(),
            TenantId = tenantId,
            DeviceId = Guid.NewGuid(),
            SessionType = "Push",
            Status = "Committed",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-5),
            CommittedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        await db.CreateSessionAsync(session);
        return session.SessionId;
    }
}
