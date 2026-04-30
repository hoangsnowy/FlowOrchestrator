using System.Diagnostics;
using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 4 — <see cref="FlowTestHostBuilder{TFlow}.WithFastPolling"/> collapses 30s manifest interval to ~100ms.</summary>
public sealed class PollingTests
{
    [Fact]
    public async Task FastPolling_collapses_30s_manifest_interval_to_under_2_seconds()
    {
        // Arrange
        await using var host = await FlowTestHost.For<PollingFlow>()
            .WithService(new PollAttemptCounter())
            .WithHandler<PollReadyStepHandler>("PollReady")
            .WithFastPolling()
            .BuildAsync();

        // Act
        var sw = Stopwatch.StartNew();
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(10));
        sw.Stop();

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"WithFastPolling should complete in <2s but took {sw.Elapsed.TotalSeconds:F2}s");
    }
}
