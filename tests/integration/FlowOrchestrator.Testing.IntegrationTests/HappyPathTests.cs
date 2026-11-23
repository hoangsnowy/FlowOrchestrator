using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 1 — three-step linear flow runs to completion.</summary>
public sealed class HappyPathTests
{
    [Fact]
    public async Task LinearFlow_runs_to_completion()
    {
        // Arrange
        await using var host = await FlowTestHost.For<LinearTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal(StepStatus.Succeeded, result.Steps["step_a"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["step_b"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["step_c"].Status);
        Assert.Equal("alpha", result.Steps["step_a"].Output.GetProperty("Echoed").GetString());
        Assert.Equal("beta", result.Steps["step_b"].Output.GetProperty("Echoed").GetString());
        Assert.Equal("gamma", result.Steps["step_c"].Output.GetProperty("Echoed").GetString());
    }

    [Fact]
    public async Task LinearFlow_result_events_are_the_derived_run_timeline()
    {
        // Arrange
        await using var host = await FlowTestHost.For<LinearTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));

        // Assert — populated without any option (2.0 derives it from the step attempts).
        Assert.Equal(
            ["run.started", "step.started", "step.completed", "step.started", "step.completed", "step.started", "step.completed", "run.completed"],
            result.Events.Select(e => e.Type));
        Assert.Equal(["step_a", "step_a", "step_b", "step_b", "step_c", "step_c"], result.Events.Skip(1).Take(6).Select(e => e.StepKey));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), result.Events.Select(e => e.Sequence));
        Assert.All(result.Events, e => Assert.Equal(result.RunId, e.RunId));
    }
}
