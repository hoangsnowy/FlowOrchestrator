using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 5 — frozen clock + FastForwardAsync drives the in-memory cron loop.</summary>
public sealed class CronTests
{
    [Fact]
    public async Task WithSystemClock_then_FastForward_fires_cron_trigger()
    {
        // Arrange
        var counter = new CronCallCounter();
        await using var host = await FlowTestHost.For<CronFlow>()
            .WithService(counter)
            .WithHandler<CronTickStepHandler>("CronTick")
            .WithSystemClock(new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero))
            .BuildAsync();

        // Act — give the cron-trigger sync a moment to register, then advance 1 minute and wait
        // for the in-memory dispatcher's next real-time tick (1s) plus the run to complete.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await host.FastForwardAsync(TimeSpan.FromMinutes(1));

        // Window must absorb (a) the 1s PeriodicTimer tick and (b) CI-side CPU contention.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && counter.Calls == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        // Assert
        Assert.True(counter.Calls >= 1, $"Cron trigger should have fired but counter={counter.Calls}.");
    }
}
