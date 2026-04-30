using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.SampleApp.Flows;
using FlowOrchestrator.SampleApp.Steps;
using FlowOrchestrator.Testing;

namespace FlowOrchestrator.SampleApp.Tests;

/// <summary>Integration test for <see cref="HelloWorldFlow"/> using <see cref="FlowTestHost"/>.</summary>
public sealed class HelloWorldFlowTests
{
    [Fact]
    public async Task HelloWorldFlow_runs_both_log_steps_to_completion()
    {
        // Arrange
        await using var host = await FlowTestHost.For<HelloWorldFlow>()
            .WithHandler<LogMessageStepHandler>("LogMessage")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(StepStatus.Succeeded, result.Steps["system_check"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["system_ready"].Status);
    }
}
