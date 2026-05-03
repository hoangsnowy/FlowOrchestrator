using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression for the v1.23.0 publish-CI flake where
/// <c>HappyPathTests.LinearFlow_runs_to_completion</c> reported only 2 of 3 step records
/// despite a successful run status. Race: a step could be dispatched
/// (<c>TryRecordDispatchAsync</c> = true, <c>EnqueueStepAsync</c> queued) but not yet
/// claimed by the consumer when <c>TryCompleteRunAsync</c> read the runtime store —
/// neither status nor claim ledgers reflected the queued step, so the run completed
/// prematurely. Fixed by also checking the dispatch ledger before completing.
/// Stresses the path by running the linear flow many times back-to-back; on a buggy
/// engine this reproduced the missing-step-record assertion within ~50 iterations.
/// </summary>
public sealed class LinearFlowTerminationStressTests
{
    [Fact]
    public async Task LinearFlow_records_all_three_steps_under_repeated_triggers()
    {
        // Arrange — single host shared across iterations to keep timing tight, like a
        // production worker draining many runs in sequence.
        await using var host = await FlowTestHost.For<LinearStressFlow>()
            .WithHandler<StressEchoStepHandler>("StressEcho")
            .BuildAsync();

        const int iterations = 50;

        // Act + Assert — every iteration MUST end with all three step records.
        for (var i = 0; i < iterations; i++)
        {
            var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(30));

            Assert.False(result.TimedOut, $"Run {i + 1}/{iterations} timed out.");
            Assert.Equal(RunStatus.Succeeded, result.Status);
            Assert.Equal(3, result.Steps.Count);
            Assert.Equal(StepStatus.Succeeded, result.Steps["step_a"].Status);
            Assert.Equal(StepStatus.Succeeded, result.Steps["step_b"].Status);
            Assert.Equal(StepStatus.Succeeded, result.Steps["step_c"].Status);
        }
    }
}
