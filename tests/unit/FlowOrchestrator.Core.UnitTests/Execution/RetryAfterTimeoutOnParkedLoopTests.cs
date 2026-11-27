using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Coverage for <c>RetryStepAsync</c> on a ForEach run that timed out: a <c>Failed</c> iteration retried
/// afterwards must actually execute on a refreshed window, while the steps the termination gate skipped
/// are not retryable at all (2.0: only a <c>Failed</c> step can be retried).
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
    public async Task RetryOfALoopChildSkippedByTheTimeout_isRejected_becauseOnlyAFailedStepCanBeRetried()
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

        // Act
        var result = await harness.Engine.RetryStepAsync(flow.Id, runId, "loop.0.work");
        await harness.DrainAsync();

        // Assert - 2.0 (RFC P-1(c)): the retry is refused before any side effect, so the run keeps its
        // TimedOut verdict and no fresh window is granted. Before 2.0 a Skipped step could be retried.
        Assert.Equal(StepRetryOutcome.NotFailed, Assert.IsType<StepRetryResult>(result).Outcome);
        Assert.Equal(0, attempts.GetValueOrDefault("loop.0.work"));
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
        Assert.NotNull((await harness.Store.GetRunControlAsync(runId))!.TimedOutAtUtc);
    }

    [Fact]
    public async Task RetryOfTheLoopStepAbandonedByTheTimeout_isRejected()
    {
        // Arrange - same timed-out run; the operator targets the ForEach itself, which the gate abandoned.
        var (harness, flow, attempts) = Build();
        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.DrainAsync();
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop"));

        // Act
        var result = await harness.Engine.RetryStepAsync(flow.Id, runId, "loop");
        await harness.DrainAsync();

        // Assert
        Assert.Equal(StepRetryOutcome.NotFailed, Assert.IsType<StepRetryResult>(result).Outcome);
        Assert.Equal(1, attempts["loop"]);
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RetryOfAFailedLoopChild_afterTheRunTimedOut_reExecutesOnAFreshWindow_andTheSweepDoesNotResurrectTheVerdict()
    {
        // Arrange - the iteration fails, the loop settles, the run finishes Failed; the deadline then lapses
        // and the run is latched TimedOut while it sits there.
        var flow = MakeLoopFlow();
        var attempts = new ConcurrentDictionary<string, int>();
        var harness = new LoopBarrierEngineHarness(
            flow,
            stepKey =>
            {
                var attempt = attempts.AddOrUpdate(stepKey, 1, static (_, current) => current + 1);
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

                return stepKey == "loop.0.work" && attempt == 1
                    ? new StepResult { Key = stepKey, Status = StepStatus.Failed, FailedReason = "transient" }
                    : new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };
            },
            new FlowRunControlOptions { DefaultRunTimeout = TimeSpan.FromHours(1) });
        var runId = await harness.TriggerAsync();
        await harness.DrainAsync();
        Assert.Equal("Failed", await harness.RunStatusAsync(runId));
        await harness.Store.MarkTimedOutAsync(runId, "Deadline exceeded");

        // Act - retry, then a sweep tick lands before the dispatched attempt runs.
        var result = await harness.Engine.RetryStepAsync(flow.Id, runId, "loop.0.work");
        await harness.Engine.EnforceDueTimeoutsAsync();
        await harness.DrainAsync();

        // Assert - the handler ran on a fresh window, and the sweep did not re-close the run on the stale
        // verdict: ExtendDeadlineAsync cleared TimedOutAtUtc and the cancel latch it set.
        Assert.Equal(StepRetryOutcome.Accepted, Assert.IsType<StepRetryResult>(result).Outcome);
        Assert.Equal(2, attempts["loop.0.work"]);
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop.0.work"));
        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.False(control.CancelRequested);
        Assert.True(control.TimeoutAtUtc > DateTimeOffset.UtcNow, "the retry must grant a deadline in the future");
        Assert.NotEqual("TimedOut", await harness.RunStatusAsync(runId));
    }
}
