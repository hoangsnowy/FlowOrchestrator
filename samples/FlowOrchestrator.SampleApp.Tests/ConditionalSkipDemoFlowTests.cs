using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.SampleApp.Flows;
using FlowOrchestrator.SampleApp.Steps;
using FlowOrchestrator.Testing;

namespace FlowOrchestrator.SampleApp.Tests;

/// <summary>
/// Integration test for <see cref="ConditionalSkipDemoFlow"/> — verifies that the engine
/// produces the expected mix of Succeeded/Failed/Skipped step statuses when a branch fails.
/// </summary>
public sealed class ConditionalSkipDemoFlowTests
{
    [Fact]
    public async Task Failed_branch_skips_happy_path_and_runs_fallback()
    {
        // Arrange
        await using var host = await FlowTestHost.For<ConditionalSkipDemoFlow>()
            .WithHandler<LogMessageStepHandler>("LogMessage")
            .WithHandler<SimulatedFailureStep>("SimulatedFailure")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(result.TimedOut);
        // Run completes despite the simulated failure because send_receipt always fires.
        Assert.Equal(StepStatus.Succeeded, result.Steps["start"].Status);
        Assert.Equal(StepStatus.Failed, result.Steps["validate_payment"].Status);
        Assert.Equal(StepStatus.Skipped, result.Steps["charge_customer"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["handle_decline"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["send_receipt"].Status);
    }
}
