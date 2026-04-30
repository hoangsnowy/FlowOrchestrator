using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 2 — handler exceptions are captured by the engine, not propagated to the caller.</summary>
public sealed class FailureTests
{
    [Fact]
    public async Task HandlerThrows_run_ends_Failed_no_exception_bubbles()
    {
        // Arrange
        await using var host = await FlowTestHost.For<FailingFlow>()
            .WithHandler<BoomStepHandler>("Boom")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(StepStatus.Failed, result.Steps["boom"].Status);
        Assert.NotNull(result.Steps["boom"].FailureReason);
        Assert.Contains("boom", result.Steps["boom"].FailureReason!, StringComparison.OrdinalIgnoreCase);
    }
}
