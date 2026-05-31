using FluentAssertions;
using SyncSession.Core.Models;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Server.Models;
using Xunit;

namespace SyncSession.UnitTests.Core;

/// <summary>
/// Tests for <see cref="ServerSyncConfiguration"/> default values and table registry behavior.
/// </summary>
public class ServerSyncConfigurationTests
{
    [Fact]
    public void ServerSyncConfiguration_ShouldHaveCorrectDefaults()
    {
        var config = new ServerSyncConfiguration();

        config.PushSharedTableThreshold.Should().Be(10000);
        config.PullSharedTableThreshold.Should().Be(10000);
        config.SessionActivityTimeoutMinutes.Should().Be(30);
        config.SharedTableCleanupIntervalMinutes.Should().Be(60);
        config.OrphanedTableCleanupDays.Should().Be(1);
        config.SessionRetentionDays.Should().Be(0);
        config.QueuePollIntervalSeconds.Should().Be(5);
        config.Tables.Should().NotBeNull();
        config.Tables.Should().BeEmpty();
    }

    [Fact]
    public void ServerSyncConfiguration_ShouldAllowCustomValues()
    {
        var config = new ServerSyncConfiguration
        {
            PushSharedTableThreshold = 5000,
            PullSharedTableThreshold = 5000,
            SessionActivityTimeoutMinutes = 60,
            SessionRetentionDays = 90,
            QueuePollIntervalSeconds = 10
        };

        config.PushSharedTableThreshold.Should().Be(5000);
        config.PullSharedTableThreshold.Should().Be(5000);
        config.SessionActivityTimeoutMinutes.Should().Be(60);
        config.SessionRetentionDays.Should().Be(90);
        config.QueuePollIntervalSeconds.Should().Be(10);
    }

    [Fact]
    public void Tables_RegisterTable_StoresConfigs()
    {
        var config = new ServerSyncConfiguration();

        config.RegisterTable<Customer>("Customers", 1);
        config.RegisterTable<Order>("Orders", 2);

        config.Tables.Should().HaveCount(2);
        config.Tables["Customers"].Priority.Should().Be(1);
        config.Tables["Orders"].Priority.Should().Be(2);
    }
}
