using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 7 — TriggerAsync returns <c>TimedOut</c> when the configured budget elapses before the flow finishes.</summary>
public sealed class TimeoutTests
{
    [Fact]
    public async Task TriggerAsync_with_short_timeout_returns_TimedOut_without_hanging()
    {
        // Arrange
        await using var host = await FlowTestHost.For<SlowFlow>()
            .WithHandler<SlowStepHandler>("Slow")
            .BuildAsync();

        // Act — handler sleeps 5s; we pass a 200ms budget.
        var result = await host.TriggerAsync(timeout: TimeSpan.FromMilliseconds(200));

        // Assert — surface TimedOut. We deliberately do NOT assert an upper-bound on
        // elapsed wall-clock time: that pattern is the classic source of CI flakiness.
        Assert.True(result.TimedOut);
    }
}
