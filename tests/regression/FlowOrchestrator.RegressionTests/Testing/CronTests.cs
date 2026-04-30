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

        // Act — advance the frozen clock past the cron's next fire time.
        // The cron sync runs on host start; advancing here triggers the dispatcher loop.
        await host.FastForwardAsync(TimeSpan.FromMinutes(1));

        // Wait for the handler to actually run. 30s budget absorbs CI CPU contention
        // and the in-memory dispatcher's 1s real-time tick. Logical event > wall clock.
        await counter.FirstCall.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.True(counter.Calls >= 1);
    }
}
