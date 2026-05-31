using System;
using FluentAssertions;
using SyncSession.Server.Models;
using Xunit;

namespace SyncSession.UnitTests.Core;

/// <summary>
/// Validation tests for <see cref="ServerSyncConfiguration"/>.
/// </summary>
public class ServerSyncConfigurationValidationTests
{
    [Fact]
    public void Validate_DefaultConfiguration_DoesNotThrow()
    {
        var config = new ServerSyncConfiguration();
        config.Invoking(c => c.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Validate_SessionActivityTimeoutOutOfRange_Throws(int value)
    {
        var config = new ServerSyncConfiguration { SessionActivityTimeoutMinutes = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*SessionActivityTimeoutMinutes*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Validate_SharedTableCleanupIntervalOutOfRange_Throws(int value)
    {
        var config = new ServerSyncConfiguration { SharedTableCleanupIntervalMinutes = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*SharedTableCleanupIntervalMinutes*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void Validate_OrphanedTableCleanupDaysOutOfRange_Throws(int value)
    {
        var config = new ServerSyncConfiguration { OrphanedTableCleanupDays = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*OrphanedTableCleanupDays*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    public void Validate_SessionRetentionDaysOutOfRange_Throws(int value)
    {
        var config = new ServerSyncConfiguration { SessionRetentionDays = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*SessionRetentionDays*");
    }

    [Theory]
    [InlineData(0)]   // 0 = never purge (default, 38B-a)
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(3650)]
    public void Validate_SessionRetentionDaysInRange_DoesNotThrow(int value)
    {
        var config = new ServerSyncConfiguration { SessionRetentionDays = value };
        config.Invoking(c => c.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Validate_QueuePollIntervalOutOfRange_Throws(int value)
    {
        var config = new ServerSyncConfiguration { QueuePollIntervalSeconds = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*QueuePollIntervalSeconds*");
    }

    [Fact]
    public void Validate_MultipleViolations_AllReportedInSingleException()
    {
        var config = new ServerSyncConfiguration
        {
            SessionActivityTimeoutMinutes = 0,
            SharedTableCleanupIntervalMinutes = 0,
            QueuePollIntervalSeconds = 0
        };

        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll(
                "SessionActivityTimeoutMinutes",
                "SharedTableCleanupIntervalMinutes",
                "QueuePollIntervalSeconds");
    }
}
