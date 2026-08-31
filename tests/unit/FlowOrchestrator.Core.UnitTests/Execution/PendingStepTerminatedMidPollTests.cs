using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Race coverage for a polling step that returns <see cref="StepStatus.Pending"/> in the same
/// window the run is cancelled or times out.
/// </summary>
/// <remarks>
/// <c>RunStepAsync</c> re-checks run control after a Pending result and returns early when the run
/// has been terminated. That early return happens <b>before</b> the reschedule — and before the
/// dispatch-ledger release — so the step is left <see cref="StepStatus.Pending"/> with a live
/// dispatch row and nothing queued to drive the next attempt. A Pending step makes
/// <c>HasInFlightWorkAsync</c> report in-flight work forever, so the run can then be closed by
/// nothing except a host restart. The window is small but reachable in practice: the cancel only
/// has to land between the entry gate and the handler returning, which for a <c>WaitForSignal</c>
/// or a polling step is the whole duration of the fetch.
/// <para>
/// The tests below close the race deterministically by requesting the cancellation from inside the
/// step handler itself, so there is no timing dependency at all.
/// </para>
/// </remarks>
public sealed class PendingStepTerminatedMidPollTests
{
    private static IFlowDefinition MakePollingFlow()
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
                ["poll"] = new StepMetadata { Type = "Pollable" },
                ["after_poll"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["poll"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    private static IFlowDefinition MakeLoopWithPollingBodyFlow()
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
                    ForEach = new List<object?> { "a" },
                    Steps = new StepCollection
                    {
                        ["wait"] = new StepMetadata { Type = "Pollable" },
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

    [Fact]
    public async Task RunCancelledWhileAPollIterationIsExecuting_stillClosesTheRun()
    {
        // Arrange - the handler cancels the run just before returning Pending, which reproduces
        // the "cancel landed after the entry gate" interleaving with no timing dependency.
        var runId = Guid.Empty;
        LoopBarrierEngineHarness? harness = null;
        harness = new LoopBarrierEngineHarness(MakePollingFlow(), stepKey =>
        {
            if (stepKey != "poll")
            {
                return new StepResult { Key = stepKey, Status = StepStatus.Succeeded };
            }

            harness!.Store.RequestCancelAsync(runId, "cancelled mid-poll").GetAwaiter().GetResult();
            return new StepResult
            {
                Key = stepKey,
                Status = StepStatus.Pending,
                DelayNextStep = TimeSpan.FromMilliseconds(1)
            };
        });

        runId = await harness.TriggerAsync();

        // Act
        await harness.DrainAsync();

        // Assert - the step must not be stranded Pending (which would keep the run in flight
        // forever), and the run must reach its run-control terminal status.
        Assert.NotEqual(StepStatus.Pending.ToString(), await harness.StepStatusAsync(runId, "poll"));
        Assert.DoesNotContain("after_poll", harness.Enqueued);
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RunTimedOutWhileAPollIterationIsExecuting_stillClosesTheRun()
    {
        // Arrange - same interleaving, driven by a lapsed deadline rather than a user cancel.
        var runId = Guid.Empty;
        LoopBarrierEngineHarness? harness = null;
        harness = new LoopBarrierEngineHarness(MakePollingFlow(), stepKey =>
        {
            if (stepKey != "poll")
            {
                return new StepResult { Key = stepKey, Status = StepStatus.Succeeded };
            }

            harness!.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1)).GetAwaiter().GetResult();
            return new StepResult
            {
                Key = stepKey,
                Status = StepStatus.Pending,
                DelayNextStep = TimeSpan.FromMilliseconds(1)
            };
        });

        runId = await harness.TriggerAsync();

        // Act
        await harness.DrainAsync();

        // Assert
        Assert.NotEqual(StepStatus.Pending.ToString(), await harness.StepStatusAsync(runId, "poll"));
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RunCancelledWhileALoopIterationIsPolling_stillClosesTheRun()
    {
        // Arrange - the same race, but the polling step is a ForEach body step, so the parked
        // loop step is also holding the run open.
        var runId = Guid.Empty;
        LoopBarrierEngineHarness? harness = null;
        harness = new LoopBarrierEngineHarness(MakeLoopWithPollingBodyFlow(), stepKey =>
        {
            if (stepKey == "loop")
            {
                return new StepResult
                {
                    Key = "loop",
                    Status = StepStatus.Running,
                    Result = new { iterations = 1 },
                    DispatchHint = new StepDispatchHint(
                        [new StepDispatchRequest("loop.0.wait", "Pollable", new Dictionary<string, object?>())])
                };
            }

            if (stepKey != "loop.0.wait")
            {
                return new StepResult { Key = stepKey, Status = StepStatus.Succeeded };
            }

            harness!.Store.RequestCancelAsync(runId, "cancelled mid-poll").GetAwaiter().GetResult();
            return new StepResult
            {
                Key = stepKey,
                Status = StepStatus.Pending,
                DelayNextStep = TimeSpan.FromMilliseconds(1)
            };
        });

        runId = await harness.TriggerAsync();

        // Act
        await harness.DrainAsync();

        // Assert - both the polling iteration and the loop step it is parked under must end up
        // terminal, otherwise the run is unclosable.
        Assert.NotEqual(StepStatus.Pending.ToString(), await harness.StepStatusAsync(runId, "loop.0.wait"));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.DoesNotContain("after_loop", harness.Enqueued);
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
    }
}
