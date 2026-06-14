using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SyncSession.Core.Constants;
using SyncSession.Core.Interfaces;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Tests the Guid &lt;-&gt; string mapping in the client-metadata extension methods. The database
/// layer only stores raw strings; these helpers own the parse/serialize and the well-known key.
/// </summary>
public class ClientDatabaseMetadataExtensionsTests
{
    [Fact]
    public async Task GetBoundTenantAsync_ValidGuid_ReturnsParsed()
    {
        var tenant = Guid.NewGuid();
        var db = new Mock<IClientDatabase>();
        db.Setup(d => d.GetClientMetadataAsync(ClientMetadataKeys.BoundTenantId))
          .ReturnsAsync(tenant.ToString());

        (await db.Object.GetBoundTenantAsync()).Should().Be(tenant);
    }

    [Fact]
    public async Task GetBoundTenantAsync_Missing_ReturnsNull()
    {
        var db = new Mock<IClientDatabase>();
        db.Setup(d => d.GetClientMetadataAsync(ClientMetadataKeys.BoundTenantId))
          .ReturnsAsync((string?)null);

        (await db.Object.GetBoundTenantAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetBoundTenantAsync_Unparseable_ReturnsNull()
    {
        var db = new Mock<IClientDatabase>();
        db.Setup(d => d.GetClientMetadataAsync(ClientMetadataKeys.BoundTenantId))
          .ReturnsAsync("not-a-guid");

        (await db.Object.GetBoundTenantAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SetBoundTenantAsync_WritesGuidStringUnderKey()
    {
        var tenant = Guid.NewGuid();
        var db = new Mock<IClientDatabase>();

        await db.Object.SetBoundTenantAsync(tenant);

        db.Verify(d => d.SetClientMetadataAsync(ClientMetadataKeys.BoundTenantId, tenant.ToString()), Times.Once);
    }
}
