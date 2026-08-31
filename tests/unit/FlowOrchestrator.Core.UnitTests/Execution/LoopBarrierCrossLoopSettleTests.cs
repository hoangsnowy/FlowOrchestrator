using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Coverage for settling a loop barrier whose last outstanding iteration step is recorded
/// <see cref="StepStatus.Skipped"/> by the continuation of a step that belongs to a
/// <b>different</b> loop.
/// </summary>
/// <remarks>
/// The graph continuation's blocked-step pass can cascade-skip any step in the run, not just a
/// descendant of the step that just finished. If the settle pass only considers the loops
/// enclosing that step, a sibling loop whose last child was skipped by that same pass has no
/// later completion left to settle it and stays <see cref="StepStatus.Running"/> forever, which
/// keeps <c>HasInFlightWorkAsync</c> true and strands the run.
/// </remarks>
public sealed class LoopBarrierCrossLoopSettleTests
{
    /// <summary>
    /// Two sibling loops. <c>loop_b</c>'s tail child depends on <c>loop_a</c>'s child by absolute
    /// runtime key, so the cascade-skip for the tail is produced by <c>loop_a</c>'s continuation.
    /// </summary>
    private static IFlowDefinition MakeSiblingLoopsFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
            },
            Steps = new StepCollection
            {
                ["loop_a"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = new List<object?> { "a0" },
                    Steps = new StepCollection
                    {
                        ["a"] = new StepMetadata { Type = "Echo" }
                    }
                },
                ["loop_b"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = new List<object?> { "b0" },
                    Steps = new StepCollection
                    {
                        ["b_head"] = new StepMetadata { Type = "Echo" },
                        ["b_tail"] = new StepMetadata
                        {
                            Type = "Echo",
                            RunAfter = new RunAfterCollection { ["loop_a.0.a"] = [StepStatus.Succeeded] }
                        }
                    }
                },
                ["after_b"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["loop_b"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    private static IStepResult ResultFor(string stepKey) => stepKey switch
    {
        "loop_a" => Park("loop_a", 1, ("loop_a.0.a", "Echo")),
        "loop_b" => Park("loop_b", 1, ("loop_b.0.b_head", "Echo")),
        "loop_a.0.a" => new StepResult
        {
            Key = stepKey,
            Status = StepStatus.Failed,
            FailedReason = "iteration blew up"
        },
        _ => new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } }
    };

    private static IStepResult Park(string loopKey, int iterations, params (string Key, string Type)[] children) =>
        new StepResult
        {
            Key = loopKey,
            Status = StepStatus.Running,
            Result = new { iterations },
            DispatchHint = new StepDispatchHint(
                [.. children.Select(c => new StepDispatchRequest(c.Key, c.Type, new Dictionary<string, object?>()))])
        };

    [Fact]
    public async Task LastIterationSkippedByAnotherLoopsContinuation_stillSettlesTheSiblingLoop()
    {
        // Arrange - both loops fan out; loop_b's head finishes while loop_a's child is still
        // queued, so loop_b's tail is still merely "waiting" at that point.
        var flow = MakeSiblingLoopsFlow();
        var harness = new LoopBarrierEngineHarness(flow, ResultFor);
        var runId = await harness.TriggerAsync();

        await harness.RunKeyAsync("loop_a");
        await harness.RunKeyAsync("loop_b");
        await harness.RunKeyAsync("loop_b.0.b_head");
        Assert.Equal(StepStatus.Running.ToString(), await harness.StepStatusAsync(runId, "loop_b"));

        // Act - loop_a's child fails. Its continuation cascade-skips loop_b.0.b_tail, which is
        // loop_b's last outstanding iteration step.
        await harness.RunKeyAsync("loop_a.0.a");
        await harness.DrainAsync();

        // Assert - loop_b has nothing left to run, so its barrier must be settled and the run
        // must reach a terminal status rather than parking on a loop nobody can finish.
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop_b.0.b_tail"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop_b"));
        Assert.NotEqual("Running", await harness.RunStatusAsync(runId));
    }
}
