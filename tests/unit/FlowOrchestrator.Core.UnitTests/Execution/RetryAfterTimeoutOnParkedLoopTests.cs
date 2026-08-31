using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Coverage for <c>RetryStepAsync</c>'s deadline refresh on a run that timed out while a ForEach was
/// parked on its completion barrier: the retried step must actually execute, which requires the
/// refreshed window to be visible to the termination gate before the step is re-entered.
/// </summary>
/// <remarks>
/// The refresh is ordered ahead of the store-level retry write so nothing — neither the gate nor the
/// periodic sweep — can observe the run as active again while the control record still carries the
/// old terminal verdict.
/// </remarks>
public sealed class RetryAfterTimeoutOnParkedLoopTests
{
    private static IFlowDefinition MakeLoopFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["loop"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = new List<object?> { "a" },
                    Steps = new StepCollection { ["work"] = new StepMetadata { Type = "Echo" } }
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

    private static (LoopBarrierEngineHarness Harness, IFlowDefinition Flow, ConcurrentDictionary<string, int> Attempts) Build()
    {
        var flow = MakeLoopFlow();
        var attempts = new ConcurrentDictionary<string, int>();
        var harness = new LoopBarrierEngineHarness(
            flow,
            stepKey =>
            {
                attempts.AddOrUpdate(stepKey, 1, static (_, current) => current + 1);
                if (stepKey == "loop")
                {
                    return new StepResult
                    {
                        Key = "loop",
                        Status = StepStatus.Running,
                        Result = new { iterations = 1 },
                        DispatchHint = new StepDispatchHint(
                            [new StepDispatchRequest("loop.0.work", "Echo", new Dictionary<string, object?>())])
                    };
                }

                return new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };
            },
            new FlowRunControlOptions { DefaultRunTimeout = TimeSpan.FromHours(1) });

        return (harness, flow, attempts);
    }

    [Fact]
    public async Task RetryOfALoopChild_afterTheRunTimedOutOnAParkedBarrier_reExecutesOnAFreshWindow()
    {
        // Arrange - the loop fans out and parks, the deadline then lapses, and the queued iteration
        // is skipped by the termination gate (which also abandons the parked loop).
        var (harness, flow, attempts) = Build();
        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.DrainAsync();

        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.0.work"));
        // The gate skips a step before the executor is reached, so the handler never ran at all.
        Assert.Equal(0, attempts.GetValueOrDefault("loop.0.work"));

        // Act
        await harness.Engine.RetryStepAsync(flow.Id, runId, "loop.0.work");
        await harness.DrainAsync();

        // Assert - the handler actually ran, so the refreshed window was in force by the time the
        // termination gate was consulted on the retried attempt.
        Assert.Equal(1, attempts.GetValueOrDefault("loop.0.work"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop.0.work"));

        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.False(control.CancelRequested);
        Assert.NotNull(control.TimeoutAtUtc);
        Assert.True(control.TimeoutAtUtc > DateTimeOffset.UtcNow, "the retry must grant a deadline in the future");
        Assert.NotEqual("TimedOut", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RetryOfTheLoopStep_afterTheRunTimedOut_reArmsTheBarrierAndSettlesIt()
    {
        // Arrange - same timed-out run, but the operator retries the ForEach itself. The retry clears
        // the dispatch ledger for "loop" only, so the re-executed handler re-arms the barrier while
        // its iteration is suppressed as already-dispatched.
        var (harness, flow, attempts) = Build();
        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.DrainAsync();
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop"));

        // Act
        await harness.Engine.RetryStepAsync(flow.Id, runId, "loop");
        await harness.DrainAsync();

        // Assert - the loop re-executed on the fresh window, its already-terminal iteration settled
        // the re-armed barrier, and the run left its TimedOut state.
        Assert.Equal(2, attempts["loop"]);
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal(1, harness.Enqueued.Count(k => k == "loop.0.work"));
        Assert.NotEqual("TimedOut", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RetryAfterATimeout_doesNotResurrectTheRunForThePeriodicSweep()
    {
        // Arrange - a sweep ticking right after a retry must not re-close the run on the stale
        // verdict: ExtendDeadlineAsync clears TimedOutAtUtc and the cancel latch it set.
        var (harness, flow, _) = Build();
        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.DrainAsync();
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));

        // Act
        await harness.Engine.RetryStepAsync(flow.Id, runId, "loop.0.work");
        await harness.Engine.EnforceDueTimeoutsAsync();
        await harness.DrainAsync();

        // Assert
        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.NotEqual("TimedOut", await harness.RunStatusAsync(runId));
    }
}
