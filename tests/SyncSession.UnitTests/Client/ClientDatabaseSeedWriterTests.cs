using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using SyncSession.Client.Seeding;
using SyncSession.Core.Constants;
using SyncSession.Core.Interfaces;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Tests that the built-in seed writer binds the local database to the seeded tenant when the
/// seed completes (CommitAsync), and does nothing for a single-tenant (null) seed.
/// </summary>
public class ClientDatabaseSeedWriterTests
{
    private static ClientDatabaseSeedWriter Build(Mock<IClientDatabase> db, Guid? tenantId) =>
        new ClientDatabaseSeedWriter(
            db.Object,
            new Mock<ITableMetadataCache>().Object,
            tenantId,
            new Mock<ILogger<ClientDatabaseSeedWriter>>().Object);

    [Fact]
    public async Task CommitAsync_MultiTenant_BindsSeededTenant()
    {
        var tenant = Guid.NewGuid();
        var db = new Mock<IClientDatabase>();

        await Build(db, tenant).CommitAsync(CancellationToken.None);

        db.Verify(d => d.SetClientMetadataAsync(ClientMetadataKeys.BoundTenantId, tenant.ToString()), Times.Once);
    }

    [Fact]
    public async Task CommitAsync_SingleTenant_NullTenant_DoesNotBind()
    {
        var db = new Mock<IClientDatabase>();

        await Build(db, null).CommitAsync(CancellationToken.None);

        db.Verify(d => d.SetClientMetadataAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
