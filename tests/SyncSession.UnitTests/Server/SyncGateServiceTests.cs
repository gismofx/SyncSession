using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.UnitTests.Server;

/// <summary>
/// Unit tests for SyncGateService.
/// Verifies enable/disable state transitions and thread safety.
/// </summary>
public class SyncGateServiceTests
{
    [Fact]
    public void IsGated_DefaultState_IsFalse()
    {
        var gate = new SyncGateService();
        gate.IsGated.Should().BeFalse();
    }

    [Fact]
    public void Enable_SetsIsGatedTrue()
    {
        var gate = new SyncGateService();
        gate.Enable();
        gate.IsGated.Should().BeTrue();
    }

    [Fact]
    public void Disable_ClearsIsGated()
    {
        var gate = new SyncGateService();
        gate.Enable();
        gate.Disable();
        gate.IsGated.Should().BeFalse();
    }

    [Fact]
    public void Disable_WithoutPriorEnable_RemainsClean()
    {
        var gate = new SyncGateService();
        gate.Disable(); // no-op, should not throw
        gate.IsGated.Should().BeFalse();
    }

    [Fact]
    public void Enable_CalledMultipleTimes_RemainsGated()
    {
        var gate = new SyncGateService();
        gate.Enable();
        gate.Enable();
        gate.IsGated.Should().BeTrue();
    }

    [Fact]
    public void ThreadSafety_ConcurrentEnableDisable_DoesNotThrow()
    {
        var gate = new SyncGateService();
        var tasks = new Task[20];

        for (int i = 0; i < 20; i++)
        {
            var i2 = i;
            tasks[i] = Task.Run(() =>
            {
                if (i2 % 2 == 0) gate.Enable();
                else gate.Disable();
            });
        }

        var act = () => Task.WaitAll(tasks);
        act.Should().NotThrow();

        // IsGated must be a valid bool regardless of which operation "won"
        var _ = gate.IsGated;
    }
}
