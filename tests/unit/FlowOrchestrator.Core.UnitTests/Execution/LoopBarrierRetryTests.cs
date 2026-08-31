using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Manual-retry coverage for the ForEach completion barrier: retrying the loop step itself,
/// retrying one of its iteration steps after the barrier already settled, and the scope of
/// <c>ResetCascadeSkippedDependentsAsync</c> for dependents that live inside the loop body.
/// </summary>
public sealed class LoopBarrierRetryTests
{
    /// <summary>Loop over two items with a single-step body, followed by a step gated on the loop.</summary>
    private static IFlowDefinition MakeLoopFlow()
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
                ["loop"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = new List<object?> { "a", "b" },
                    ConcurrencyLimit = 2,
                    Steps = new StepCollection
                    {
                        ["work"] = new StepMetadata { Type = "Echo" }
                    }
                },
                ["after_loop"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    /// <summary>
    /// Single-iteration loop whose body pairs a failing step with a dependent that can therefore
    /// only ever be cascade-skipped.
    /// </summary>
    private static IFlowDefinition MakeFailingBodyFlow()
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
                ["loop"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = new List<object?> { "only" },
                    Steps = new StepCollection
                    {
                        ["boom"] = new StepMetadata { Type = "Boom" },
                        ["never"] = new StepMetadata
                        {
                            Type = "Echo",
                            RunAfter = new RunAfterCollection { ["boom"] = [StepStatus.Succeeded] }
                        }
                    }
                },
                ["after_loop"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    private static IStepResult ParkedLoop(int iterations, params string[] childKeys) => new StepResult
    {
        Key = "loop",
        Status = StepStatus.Running,
        Result = new { iterations },
        DispatchHint = new StepDispatchHint(
            [.. childKeys.Select(k => new StepDispatchRequest(k, "Echo", new Dictionary<string, object?>()))])
    };

    private static IStepResult Succeeded(string stepKey) =>
        new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };

    [Fact]
    public async Task RetryingTheLoopStep_afterItSettled_reSettlesWithoutReDispatchingIterations()
    {
        // Arrange - a complete run: the loop settled, the downstream step ran.
        var flow = MakeLoopFlow();
        var harness = new LoopBarrierEngineHarness(
            flow,
            stepKey => stepKey == "loop"
                ? ParkedLoop(2, "loop.0.work", "loop.1.work")
                : Succeeded(stepKey));

        var runId = await harness.TriggerAsync();
        await harness.DrainAsync();
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));

        // Act - re-run the loop step. The retry clears the dispatch ledger for "loop" only, so
        // the re-armed barrier has to settle off the iterations' existing terminal rows.
        await harness.Engine.RetryStepAsync(flow.Id, runId, "loop");
        await harness.DrainAsync();

        // Assert
        Assert.Equal(1, harness.Enqueued.Count(k => k == "loop.0.work"));
        Assert.Equal(1, harness.Enqueued.Count(k => k == "loop.1.work"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RetryingAnIterationStep_afterTheLoopSettled_leavesTheLoopSettledAndClosesTheRun()
    {
        // Arrange - the first iteration fails, so the run finishes Failed with the loop settled.
        var flow = MakeLoopFlow();
        var attempts = new ConcurrentDictionary<string, int>();
        var harness = new LoopBarrierEngineHarness(flow, stepKey =>
        {
            if (stepKey == "loop")
            {
                return ParkedLoop(2, "loop.0.work", "loop.1.work");
            }

            var attempt = attempts.AddOrUpdate(stepKey, 1, static (_, current) => current + 1);
            if (stepKey == "loop.0.work" && attempt == 1)
            {
                return new StepResult { Key = stepKey, Status = StepStatus.Failed, FailedReason = "transient" };
            }

            return Succeeded(stepKey);
        });

        var runId = await harness.TriggerAsync();
        await harness.DrainAsync();
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal("Failed", await harness.RunStatusAsync(runId));

        // Act - retry the failed iteration; it succeeds on the second attempt.
        await harness.Engine.RetryStepAsync(flow.Id, runId, "loop.0.work");
        await harness.DrainAsync();

        // Assert - the already-settled loop must not be re-opened, the downstream step must not be
        // dispatched twice, and the run must be re-closed rather than left Running by the retry.
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop.0.work"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal(1, harness.Enqueued.Count(k => k == "after_loop"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RetryingAFailedIterationStep_doesNotResetItsCascadeSkippedSiblingInsideTheLoop()
    {
        // Arrange - "never" is cascade-skipped because "boom" failed in the same iteration.
        var flow = MakeFailingBodyFlow();
        var attempts = new ConcurrentDictionary<string, int>();
        var harness = new LoopBarrierEngineHarness(flow, stepKey =>
        {
            if (stepKey == "loop")
            {
                return ParkedLoop(1, "loop.0.boom");
            }

            var attempt = attempts.AddOrUpdate(stepKey, 1, static (_, current) => current + 1);
            if (stepKey == "loop.0.boom" && attempt == 1)
            {
                return new StepResult { Key = stepKey, Status = StepStatus.Failed, FailedReason = "boom" };
            }

            return Succeeded(stepKey);
        });

        var runId = await harness.TriggerAsync();
        await harness.DrainAsync();
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.0.never"));

        // Act
        await harness.Engine.RetryStepAsync(flow.Id, runId, "loop.0.boom");
        await harness.DrainAsync();

        // Assert - documented limitation: ComputeTransitiveDescendants walks only the TOP-LEVEL
        // manifest, so a dependent that lives inside the loop body keeps its cascade-skip record
        // and is never re-evaluated. The retried step succeeds and the run is re-classified, but
        // the iteration tail stays skipped. Pinned here so a future change to loop-scoped retry
        // is a deliberate decision rather than an accident.
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop.0.boom"));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.0.never"));
        Assert.DoesNotContain("loop.0.never", harness.Enqueued);
        Assert.NotEqual("Running", await harness.RunStatusAsync(runId));
    }
}
