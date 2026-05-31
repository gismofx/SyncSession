using System;
using FluentAssertions;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Core;

/// <summary>
/// Validation tests for <see cref="ClientSyncConfiguration"/>.
/// </summary>
public class ClientSyncConfigurationValidationTests
{
    [Fact]
    public void Validate_DefaultConfiguration_DoesNotThrow()
    {
        var config = new ClientSyncConfiguration();
        config.Invoking(c => c.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Validate_PushBatchSizeOutOfRange_Throws(int value)
    {
        var config = new ClientSyncConfiguration { PushBatchSize = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*PushBatchSize*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Validate_PullBatchSizeOutOfRange_Throws(int value)
    {
        var config = new ClientSyncConfiguration { PullBatchSize = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*PullBatchSize*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Validate_PushStatusPollIntervalBelowMinimum_Throws(int value)
    {
        var config = new ClientSyncConfiguration { PushStatusPollIntervalMs = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*PushStatusPollIntervalMs*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_PushStatusTimeoutBelowMinimum_Throws(int value)
    {
        var config = new ClientSyncConfiguration { PushStatusTimeoutSeconds = value };
        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*PushStatusTimeoutSeconds*");
    }

    [Fact]
    public void Validate_MultipleViolations_AllReportedInSingleException()
    {
        var config = new ClientSyncConfiguration
        {
            PushBatchSize = 0,
            PullBatchSize = 0,
            PushStatusPollIntervalMs = 0
        };

        config.Invoking(c => c.Validate())
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll("PushBatchSize", "PullBatchSize", "PushStatusPollIntervalMs");
    }
}
