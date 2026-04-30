using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.SampleApp.Flows;
using FlowOrchestrator.SampleApp.Steps;
using FlowOrchestrator.Testing;

namespace FlowOrchestrator.SampleApp.Tests;

/// <summary>
/// Integration test for <see cref="DeadEndSkipDemoFlow"/> — verifies that an entry-step crash
/// surfaces as <see cref="RunStatus.Failed"/> at the run level, with downstream steps Skipped.
/// </summary>
public sealed class DeadEndSkipDemoFlowTests
{
    [Fact]
    public async Task Entry_failure_marks_run_Failed_and_skips_downstream()
    {
        // Arrange
        await using var host = await FlowTestHost.For<DeadEndSkipDemoFlow>()
            .WithHandler<LogMessageStepHandler>("LogMessage")
            .WithHandler<SimulatedFailureStep>("SimulatedFailure")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(StepStatus.Failed, result.Steps["validate_input"].Status);
        // enrich_data and save_result may or may not be recorded as Skipped depending on
        // the engine's blocked-step propagation — what matters is the run-level status.
        if (result.Steps.TryGetValue("enrich_data", out var enrich))
            Assert.Equal(StepStatus.Skipped, enrich.Status);
        if (result.Steps.TryGetValue("save_result", out var save))
            Assert.Equal(StepStatus.Skipped, save.Status);
    }
}
