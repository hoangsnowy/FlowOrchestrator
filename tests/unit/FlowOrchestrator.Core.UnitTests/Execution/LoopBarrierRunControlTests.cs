using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Interaction coverage for the ForEach completion barrier (issue #169) and the run-control
/// termination gate: cancellation, a lapsed run deadline, and the periodic timeout sweep must
/// all be able to close a run whose loop step is parked in <see cref="StepStatus.Running"/>.
/// </summary>
/// <remarks>
/// The barrier keeps the loop step <see cref="StepStatus.Running"/> until every iteration is
/// terminal, and <c>HasInFlightWorkAsync</c> treats <see cref="StepStatus.Running"/> as
/// in-flight work. A cancelled or timed-out iteration exits <c>RunStepAsync</c> through the
/// termination gate, which bypasses the graph continuation where barriers are normally settled,
/// so the gate must resolve the parked loop itself (recording it <see cref="StepStatus.Skipped"/>)
/// or the run can never reach a terminal status. Every test drives the engine synchronously
/// through <see cref="LoopBarrierEngineHarness"/>, so no assertion depends on wall-clock timing.
/// </remarks>
public sealed class LoopBarrierRunControlTests
{
    /// <summary>Builds a flow shaped loop (ForEach over two items, body "work") then "after_loop".</summary>
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

    /// <summary>Mimics <see cref="ForEachStepHandler"/>: the loop parks on its barrier, everything else succeeds.</summary>
    private static IStepResult ResultFor(string stepKey)
    {
        if (stepKey != "loop")
        {
            return new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };
        }

        return new StepResult
        {
            Key = "loop",
            Status = StepStatus.Running,
            Result = new { iterations = 2 },
            DispatchHint = new StepDispatchHint(
            [
                new StepDispatchRequest("loop.0.work", "Echo", new Dictionary<string, object?>()),
                new StepDispatchRequest("loop.1.work", "Echo", new Dictionary<string, object?>())
            ])
        };
    }

    private static async Task<(LoopBarrierEngineHarness Harness, Guid RunId)> StartAndParkLoopAsync()
    {
        var harness = new LoopBarrierEngineHarness(MakeLoopFlow(), ResultFor);
        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");   // fans out and parks on the barrier
        return (harness, runId);
    }

    [Fact]
    public async Task CancelRequested_whileLoopParkedOnBarrier_runStillReachesTerminalStatus()
    {
        // Arrange - loop fanned out and is parked Running; both iterations are still queued.
        var (harness, runId) = await StartAndParkLoopAsync();
        Assert.Equal(StepStatus.Running.ToString(), await harness.StepStatusAsync(runId, "loop"));

        // Act - cancel the run, then let the two queued iterations drain through the
        // termination gate at the top of RunStepAsync.
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");
        await harness.DrainAsync();

        // Assert - every iteration is terminal, so nothing can still advance the run; it must
        // not be left Running forever.
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.0.work"));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.1.work"));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.DoesNotContain("after_loop", harness.Enqueued);
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RunDeadlineLapsed_whileLoopParkedOnBarrier_runStillReachesTerminalStatus()
    {
        // Arrange - same parked loop, but terminated by a lapsed deadline rather than a cancel.
        var (harness, runId) = await StartAndParkLoopAsync();
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await harness.DrainAsync();

        // Assert
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.0.work"));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.1.work"));
        Assert.DoesNotContain("after_loop", harness.Enqueued);
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task CancelRequested_afterFirstIterationRan_doesNotDispatchDownstreamOfTheLoop()
    {
        // Arrange - the gate is only consulted at step entry, so the first iteration runs
        // normally; the cancel lands before the second one, which therefore resolves the parked
        // loop while the run is already latched Cancelled.
        var (harness, runId) = await StartAndParkLoopAsync();

        // Act
        await harness.RunKeyAsync("loop.0.work");
        await harness.Store.RequestCancelAsync(runId, "cancelled mid-loop");
        await harness.DrainAsync();

        // Assert - the step gated on the loop must never be dispatched for a cancelled run,
        // and the run must be closed as Cancelled.
        Assert.DoesNotContain("after_loop", harness.Enqueued);
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
    }

    /// <summary>
    /// Loop whose body has two steps: the entry child and a dependent that can only run after it.
    /// A cancel that skips the entry child leaves the dependent with no status row at all, because
    /// the termination gate never runs the blocked-step pass that would cascade-skip it.
    /// </summary>
    private static IFlowDefinition MakeMultiStepBodyLoopFlow()
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
                        ["wait"] = new StepMetadata { Type = "WaitForSignal" },
                        ["consume"] = new StepMetadata
                        {
                            Type = "Echo",
                            RunAfter = new RunAfterCollection { ["wait"] = [StepStatus.Succeeded] }
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

    private static IStepResult MultiStepBodyResultFor(string stepKey)
    {
        if (stepKey != "loop")
        {
            return new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };
        }

        return new StepResult
        {
            Key = "loop",
            Status = StepStatus.Running,
            Result = new { iterations = 2 },
            DispatchHint = new StepDispatchHint(
            [
                new StepDispatchRequest("loop.0.wait", "WaitForSignal", new Dictionary<string, object?>()),
                new StepDispatchRequest("loop.1.wait", "WaitForSignal", new Dictionary<string, object?>())
            ])
        };
    }

    [Fact]
    public async Task CancelRequested_whileLoopWithMultiStepBodyIsParked_runStillReachesTerminalStatus()
    {
        // Arrange - issue #169's own shape: the loop body is wait -> consume, and only the entry
        // child is fanned out. Cancelling here skips both waits but leaves both "consume" steps
        // without any status row, so an "all iterations terminal" settle can never fire.
        var harness = new LoopBarrierEngineHarness(MakeMultiStepBodyLoopFlow(), MultiStepBodyResultFor);
        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");

        // Act
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");
        await harness.DrainAsync();

        // Assert - the parked loop must not be able to hold the run open forever.
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.0.wait"));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.1.wait"));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.DoesNotContain("after_loop", harness.Enqueued);
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task DuplicateIdempotentTrigger_whileTheLoopIsParked_reusesTheRunAndDoesNotFanOutAgain()
    {
        // Arrange - first trigger carries an idempotency key and parks its loop on the barrier.
        var harness = new LoopBarrierEngineHarness(MakeLoopFlow(), ResultFor);
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString() };
        var firstRunId = await harness.TriggerAsync(headers: headers);
        await harness.RunKeyAsync("loop");

        // Act - the caller retries the same request while the first run is still mid-loop.
        var secondRunId = await harness.TriggerAsync(headers: headers);

        // Assert - the duplicate must resolve to the original run without starting a second run
        // and without re-dispatching the loop step (which would re-arm the barrier and double
        // the fan-out).
        Assert.Equal(firstRunId, secondRunId);
        Assert.Single(await harness.Store.GetRunsAsync());
        Assert.Equal(1, harness.Enqueued.Count(k => k == "loop"));
        Assert.Equal(StepStatus.Running.ToString(), await harness.StepStatusAsync(firstRunId, "loop"));

        // ...and the original run still completes normally once its iterations drain.
        await harness.DrainAsync();
        Assert.Equal("Succeeded", await harness.RunStatusAsync(firstRunId));
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_withLoopParkedAndIterationsQueued_leavesRunToTheDispatchGate()
    {
        // Arrange - deadline lapsed while the loop is parked and its iterations are still queued.
        var (harness, runId) = await StartAndParkLoopAsync();
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act - the sweep must not latch a run that still has queued work (documented
        // HasInFlightWorkAsync contract), so the run stays Running until the iterations drain.
        await harness.Engine.EnforceDueTimeoutsAsync();
        var statusAfterSweep = await harness.RunStatusAsync(runId);
        await harness.DrainAsync();

        // Assert
        Assert.Equal("Running", statusAfterSweep);
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
    }
}
