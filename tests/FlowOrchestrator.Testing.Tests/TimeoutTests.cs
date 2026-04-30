using System.Diagnostics;
using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 7 — TriggerAsync returns within the configured budget without hanging.</summary>
public sealed class TimeoutTests
{
    [Fact]
    public async Task TriggerAsync_with_short_timeout_returns_TimedOut_without_hanging()
    {
        // Arrange
        await using var host = await FlowTestHost.For<SlowFlow>()
            .WithHandler<SlowStepHandler>("Slow")
            .BuildAsync();

        // Act
        var sw = Stopwatch.StartNew();
        var result = await host.TriggerAsync(timeout: TimeSpan.FromMilliseconds(200));
        sw.Stop();

        // Assert — must surface TimedOut and complete in well under the handler's 5-second sleep.
        Assert.True(result.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"TriggerAsync should respect the 200ms timeout but took {sw.Elapsed.TotalSeconds:F2}s");
    }
}
