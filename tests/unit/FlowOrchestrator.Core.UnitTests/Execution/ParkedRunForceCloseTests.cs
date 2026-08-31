using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Coverage for the guarded force-close the periodic timeout sweep performs on a run whose only
/// remaining work is <i>parked</i>: a <c>WaitForSignal</c> registered without a timeout parks for
/// 24 hours, and a long-interval poll for its whole interval, so a cancelled or timed-out run would
/// otherwise sit <c>Running</c> until that timer fires.
/// </summary>
/// <remarks>
/// Every test drives the engine synchronously through <see cref="LoopBarrierEngineHarness"/> and sets
/// deadlines explicitly in the past, so nothing here depends on wall-clock timing or sleeps. The
/// guard must stay conservative — a genuinely <see cref="StepStatus.Running"/> step, a claimed key
/// with no status row, or a dispatched key with no status row all mean work can still advance, and
/// the sweep must then leave the run alone.
/// </remarks>
public sealed class ParkedRunForceCloseTests
{
    private const string ParkedStepKey = "wait";
    private const string DownstreamStepKey = "after_wait";

    /// <summary>Counts published <see cref="RunCompletedEvent"/>s so tests can assert exactly-once completion.</summary>
    private sealed class CountingNotifier : IFlowEventNotifier
    {
        private int _runCompleted;

        /// <summary>Number of <see cref="RunCompletedEvent"/>s published so far.</summary>
        public int RunCompletedCount => Volatile.Read(ref _runCompleted);

        /// <inheritdoc/>
        public ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default)
        {
            if (evt is RunCompletedEvent)
            {
                Interlocked.Increment(ref _runCompleted);
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A parked poller followed by a step gated on its success.</summary>
    private static IFlowDefinition MakeParkedPollFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                [ParkedStepKey] = new StepMetadata { Type = "WaitForSignal" },
                [DownstreamStepKey] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { [ParkedStepKey] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    /// <summary>A ForEach whose single iteration parks on a signal, plus a step gated on the loop.</summary>
    private static IFlowDefinition MakeParkedLoopFlow()
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
                    Steps = new StepCollection { ["wait"] = new StepMetadata { Type = "WaitForSignal" } }
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

    /// <summary>Parks for 24 hours, the WaitForSignal handler's indefinite interval.</summary>
    private static IStepResult IndefinitePark(string stepKey) => new StepResult
    {
        Key = stepKey,
        Status = StepStatus.Pending,
        DelayNextStep = TimeSpan.FromHours(24)
    };

    private static IStepResult Succeeded(string stepKey) =>
        new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };

    private static async Task<(LoopBarrierEngineHarness Harness, Guid RunId)> ParkedPollRunAsync(
        IFlowEventNotifier? notifier = null)
    {
        var harness = new LoopBarrierEngineHarness(
            MakeParkedPollFlow(),
            key => key == ParkedStepKey ? IndefinitePark(key) : Succeeded(key),
            eventNotifier: notifier);

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync(ParkedStepKey);   // parks for 24 h and re-queues itself
        return (harness, runId);
    }

    [Fact]
    public async Task Sweep_cancelledRunParkedOnAnIndefinitePoll_forceClosesItAsCancelled()
    {
        // Arrange - the poller is parked with a 24 h wake-up and there is no deadline at all, so
        // pre-fix nothing in the system would look at this run again for a day.
        var (harness, runId) = await ParkedPollRunAsync();
        Assert.Equal(StepStatus.Pending.ToString(), await harness.StepStatusAsync(runId, ParkedStepKey));
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, ParkedStepKey));
        Assert.Equal("Run is Cancelled.", await harness.StepReasonAsync(runId, ParkedStepKey));
        Assert.DoesNotContain(DownstreamStepKey, harness.Enqueued);
    }

    [Fact]
    public async Task Sweep_lapsedDeadlineOnARunParkedOnAnIndefinitePoll_latchesTimedOutAndForceCloses()
    {
        // Arrange
        var (harness, runId) = await ParkedPollRunAsync();
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert - the timeout is latched on the control record and the run reports TimedOut, never
        // Cancelled, even though MarkTimedOutAsync also sets the cancel latch.
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.NotNull(control!.TimedOutAtUtc);
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, ParkedStepKey));
        Assert.Equal("Run is TimedOut.", await harness.StepReasonAsync(runId, ParkedStepKey));
    }

    [Fact]
    public async Task Sweep_parkedRunWithNoRunControlVerdict_isLeftAlone()
    {
        // Arrange - parked, but neither cancelled nor past a deadline. The sweep has no mandate.
        var (harness, runId) = await ParkedPollRunAsync();

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Running", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Pending.ToString(), await harness.StepStatusAsync(runId, ParkedStepKey));
    }

    [Fact]
    public async Task Sweep_cancelledRunWithAGenuinelyRunningStep_isLeftAlone()
    {
        // Arrange - one step parked, one step mid-handler (status row written, no result yet). A
        // worker owns that step, so force-closing would race a live execution.
        var (harness, runId) = await ParkedPollRunAsync();
        await harness.Store.RecordStepStartAsync(runId, "live_step", "Work", null, null);
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Running", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Pending.ToString(), await harness.StepStatusAsync(runId, ParkedStepKey));
    }

    [Fact]
    public async Task Sweep_cancelledRunWithAClaimedButUnrecordedStep_isLeftAlone()
    {
        // Arrange - a worker is between TryClaimStepAsync and RecordStepStartAsync, so the key has a
        // claim but no status row yet.
        var (harness, runId) = await ParkedPollRunAsync();
        Assert.True(await harness.Store.TryClaimStepAsync(runId, "about_to_start"));
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Running", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Sweep_cancelledRunWithADispatchedButUnstartedStep_isLeftAlone()
    {
        // Arrange - a step is queued in the runtime but has not been picked up, so it has a dispatch
        // ledger row and no status row.
        var (harness, runId) = await ParkedPollRunAsync();
        Assert.True(await harness.Store.TryRecordDispatchAsync(runId, "queued_step"));
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Running", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Sweep_cancelledRunWithAForEachBarrierParkedOverAPendingIteration_forceClosesBoth()
    {
        // Arrange - the issue #169 shape: the loop step is Running only because its barrier is armed,
        // and its single iteration is parked on a 24 h signal wait.
        var harness = new LoopBarrierEngineHarness(MakeParkedLoopFlow(), key => key switch
        {
            "loop" => new StepResult
            {
                Key = "loop",
                Status = StepStatus.Running,
                Result = new { iterations = 1 },
                DispatchHint = new StepDispatchHint(
                    [new StepDispatchRequest("loop.0.wait", "WaitForSignal", new Dictionary<string, object?>())])
            },
            "loop.0.wait" => IndefinitePark(key),
            _ => Succeeded(key)
        });

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");
        await harness.RunKeyAsync("loop.0.wait");
        Assert.Equal(StepStatus.Running.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal(StepStatus.Pending.ToString(), await harness.StepStatusAsync(runId, "loop.0.wait"));
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert - both the parked iteration and the loop step it is parked under become terminal,
        // and the step gated on the loop is never dispatched.
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop.0.wait"));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.DoesNotContain("after_loop", harness.Enqueued);
    }

    [Fact]
    public async Task Sweep_forEachStillMidFanOut_isLeftAlone()
    {
        // Arrange - the loop step is Running because a worker is inside ForEachStepHandler, not
        // because its barrier is armed: no fan-out output is persisted yet. Another step is parked,
        // so only the missing iteration count distinguishes the two cases.
        var (harness, runId) = await ParkedPollRunAsync();
        await harness.Store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert - refused: the manifest has no scoped step under that key and no iteration count
        // was ever written, so the sweep cannot prove the loop is parked.
        Assert.Equal("Running", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Pending.ToString(), await harness.StepStatusAsync(runId, ParkedStepKey));
    }

    [Fact]
    public async Task ForceClosedRun_whenTheParkedStepLaterWakes_thePickupIsAHarmlessSkip()
    {
        // Arrange - the parked poll re-queued itself before the sweep force-closed the run, so the
        // runtime still holds a message for it. That worker must not resurrect the run.
        var notifier = new CountingNotifier();
        var (harness, runId) = await ParkedPollRunAsync(notifier);
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");
        await harness.Engine.EnforceDueTimeoutsAsync();
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
        Assert.Contains(ParkedStepKey, harness.PendingKeys);

        // Act - the queued wake-up finally fires.
        await harness.DrainAsync();

        // Assert - the entry gate at the top of RunStepAsync records the same Skipped outcome, the
        // downstream step is never dispatched, the run stays Cancelled, and CompleteRunIfActiveAsync
        // keeps the lifecycle event exactly-once.
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, ParkedStepKey));
        Assert.Equal("Run is Cancelled.", await harness.StepReasonAsync(runId, ParkedStepKey));
        Assert.DoesNotContain(DownstreamStepKey, harness.Enqueued);
        Assert.Equal(1, notifier.RunCompletedCount);
    }

    [Fact]
    public async Task Sweep_calledTwiceOnAParkedCancelledRun_completesItExactlyOnce()
    {
        // Arrange - mimics two replicas ticking their sweeps concurrently.
        var notifier = new CountingNotifier();
        var (harness, runId) = await ParkedPollRunAsync(notifier);
        await harness.Store.RequestCancelAsync(runId, "cancelled by operator");

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
        Assert.Equal(1, notifier.RunCompletedCount);
    }
}
