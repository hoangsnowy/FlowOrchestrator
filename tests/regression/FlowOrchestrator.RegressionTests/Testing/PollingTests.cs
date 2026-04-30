using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 4 — <see cref="FlowTestHostBuilder{TFlow}.WithFastPolling"/> collapses 30s manifest interval so the test completes within the trigger budget.</summary>
public sealed class PollingTests
{
    [Fact]
    public async Task FastPolling_collapses_manifest_interval_so_run_succeeds_within_budget()
    {
        // Arrange
        var counter = new PollAttemptCounter();
        await using var host = await FlowTestHost.For<PollingFlow>()
            .WithService(counter)
            .WithHandler<PollReadyStepHandler>("PollReady")
            .WithFastPolling()
            .BuildAsync();

        // Act — trigger budget is the only timing assertion. Without WithFastPolling
        // the 30s manifest interval would push the run past the 10s timeout.
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(10));

        // Assert — correct behaviour, not wall-clock speed.
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
    }
}
