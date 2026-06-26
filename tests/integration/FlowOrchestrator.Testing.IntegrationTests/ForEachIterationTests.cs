using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// End-to-end coverage for ForEach loop fan-out through the real engine + InMemory runtime.
/// Regression for the dispatch-hint guard that treated each runtime loop-child key
/// (<c>{loop}.{index}.{child}</c>) as a static DAG step and threw, leaving the loop step
/// Succeeded while its children never dispatched and the downstream step never ran.
/// </summary>
public sealed class ForEachIterationTests
{
    [Fact]
    public async Task ForEachWithItems_dispatchesEveryChild_andRunsDownstream()
    {
        // Arrange
        await using var host = await FlowTestHost.For<ForEachTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { items = new[] { "a", "b", "c" } },
            timeout: TimeSpan.FromSeconds(15));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["process_items"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["finalize"].Status);
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(StepStatus.Succeeded, result.Steps[$"process_items.{index}.iterate"].Status);
        }
    }

    [Fact]
    public async Task ForEachWithEmptyCollection_completesWithoutIterations()
    {
        // Arrange
        await using var host = await FlowTestHost.For<ForEachTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { items = Array.Empty<string>() },
            timeout: TimeSpan.FromSeconds(15));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["finalize"].Status);
        Assert.DoesNotContain(result.Steps.Keys, key => key.StartsWith("process_items.0.", StringComparison.Ordinal));
    }
}
