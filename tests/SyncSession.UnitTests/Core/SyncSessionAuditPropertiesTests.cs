using FluentAssertions;
using SyncSession.Core.Constants;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Core;

/// <summary>
/// Verifies SessionRecord audit properties (38l) and SESSION_TYPE_SEED constant.
/// </summary>
public class SyncSessionAuditPropertiesTests
{
    [Fact]
    public void SyncSession_NewInstance_HasCorrectDefaults()
    {
        var session = new SessionRecord();

        session.DeviceId.Should().BeNull();
        session.UserId.Should().BeNull();
        session.UserDisplayName.Should().BeNull();
        session.TotalRows.Should().Be(0);
        session.RowCountsJson.Should().BeNull();
    }

    [Fact]
    public void SyncSession_AuditProperties_AreSettable()
    {
        var deviceId = Guid.NewGuid();
        var session = new SessionRecord
        {
            DeviceId = deviceId,
            UserId = "user-123",
            UserDisplayName = "Jane Doe",
            TotalRows = 500,
            RowCountsJson = "{\"Customers\":200,\"Orders\":300}"
        };

        session.DeviceId.Should().Be(deviceId);
        session.UserId.Should().Be("user-123");
        session.UserDisplayName.Should().Be("Jane Doe");
        session.TotalRows.Should().Be(500);
        session.RowCountsJson.Should().Be("{\"Customers\":200,\"Orders\":300}");
    }

    [Fact]
    public void SyncConstants_SessionTypeSeed_Exists()
    {
        SyncConstants.SESSION_TYPE_SEED.Should().Be("Seed");
    }
}
