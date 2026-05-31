using FluentAssertions;
using SyncSession.Core.Models;
using SyncSession.Samples.Shared.Entities;
using Xunit;

namespace SyncSession.UnitTests.Core;

/// <summary>
/// Tests for <see cref="ClientSyncConfiguration"/> default values and table registry behavior.
/// </summary>
public class ClientSyncConfigurationTests
{
    [Fact]
    public void ClientSyncConfiguration_ShouldHaveCorrectDefaults()
    {
        var config = new ClientSyncConfiguration();

        config.PushBatchSize.Should().Be(1000);
        config.PullBatchSize.Should().Be(1000);
        config.PushStatusPollIntervalMs.Should().Be(1000);
        config.PushStatusTimeoutSeconds.Should().Be(300);
        config.Tables.Should().NotBeNull();
        config.Tables.Should().BeEmpty();
    }

    [Fact]
    public void ClientSyncConfiguration_ShouldAllowCustomValues()
    {
        var config = new ClientSyncConfiguration
        {
            PushBatchSize = 500,
            PullBatchSize = 750,
            PushStatusPollIntervalMs = 2000,
            PushStatusTimeoutSeconds = 120
        };

        config.PushBatchSize.Should().Be(500);
        config.PullBatchSize.Should().Be(750);
        config.PushStatusPollIntervalMs.Should().Be(2000);
        config.PushStatusTimeoutSeconds.Should().Be(120);
    }

    [Fact]
    public void BatchSize_ConvenienceSetter_UpdatesBothBatchSizes()
    {
        var config = new ClientSyncConfiguration { BatchSize = 250 };

        config.PushBatchSize.Should().Be(250);
        config.PullBatchSize.Should().Be(250);
    }

    [Fact]
    public void Tables_RegisterTable_StoresConfigs()
    {
        var config = new ClientSyncConfiguration();

        config.RegisterTable<Customer>("Customers", 1);
        config.RegisterTable<Order>("Orders", 2);

        config.Tables.Should().HaveCount(2);
        config.Tables["Customers"].Priority.Should().Be(1);
        config.Tables["Orders"].Priority.Should().Be(2);
    }
}
