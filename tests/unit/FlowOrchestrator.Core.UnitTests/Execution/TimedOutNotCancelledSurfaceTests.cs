using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Pins that the documented <c>MarkTimedOutAsync</c> quirk — it also sets the cancellation latch so
/// in-flight steps short-circuit on their next dispatch — never leaks to a user-visible surface as a
/// cancellation. A timed-out run must report <c>TimedOut</c> on the run row, in every step's skip
/// reason, and on the published <see cref="RunCompletedEvent"/>.
/// </summary>
/// <remarks>
/// The conflation is real and deliberate: <c>FlowRunControlRecord.CancelRequested</c> is
/// <see langword="true"/> on a timed-out run. Both readers of that record —
/// <c>ResolveTerminationStatusAsync</c> (dispatch-time gate) and <c>EnforceDueTimeoutsAsync</c>
/// (periodic sweep) — must therefore check <c>TimedOutAtUtc</c> <b>before</b> <c>CancelRequested</c>.
/// These tests exist so a future reordering of those branches fails loudly.
/// </remarks>
public sealed class TimedOutNotCancelledSurfaceTests
{
    /// <summary>Captures every published lifecycle event so tests can assert on their payloads.</summary>
    private sealed class RecordingNotifier : IFlowEventNotifier
    {
        /// <summary>Every event published, in order.</summary>
        public ConcurrentQueue<FlowLifecycleEvent> Events { get; } = new();

        /// <inheritdoc/>
        public ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default)
        {
            Events.Enqueue(evt);
            return ValueTask.CompletedTask;
        }
    }

    private static IFlowDefinition MakeTwoStepFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["first"] = new StepMetadata { Type = "Work" },
                ["second"] = new StepMetadata
                {
                    Type = "Work",
                    RunAfter = new RunAfterCollection { ["first"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    private static string? CompletedStatus(RecordingNotifier notifier) =>
        notifier.Events.OfType<RunCompletedEvent>().SingleOrDefault()?.Status;

    [Fact]
    public async Task DispatchTimeGate_onALapsedDeadline_reportsTimedOutOnEverySurface()
    {
        // Arrange - "first" runs before the deadline lapses; the deadline then lapses, so "second"
        // hits the dispatch-time gate, which latches the timeout via MarkTimedOutAsync (setting the
        // cancel latch as a side effect) and skips the step.
        var notifier = new RecordingNotifier();
        var flow = MakeTwoStepFlow();
        var harness = new LoopBarrierEngineHarness(
            flow,
            key => new StepResult { Key = key, Status = StepStatus.Succeeded, Result = new { ok = true } },
            eventNotifier: notifier);

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("first");
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await harness.DrainAsync();

        // Assert - the storage-level conflation is present...
        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.NotNull(control!.TimedOutAtUtc);
        Assert.True(control.CancelRequested);

        // ...but every user-visible surface says TimedOut.
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
        Assert.Equal(StepStatus.Skipped.ToString(), await harness.StepStatusAsync(runId, "second"));
        Assert.Equal("Run is TimedOut.", await harness.StepReasonAsync(runId, "second"));
        Assert.Equal("TimedOut", CompletedStatus(notifier));
    }

    [Fact]
    public async Task PeriodicSweep_afterTheGateAlreadyLatchedTheTimeout_stillReportsTimedOut()
    {
        // Arrange - the run is latched TimedOut (so CancelRequested is true) but was left Running.
        // A sweep that checked CancelRequested first would finalise it as Cancelled.
        var notifier = new RecordingNotifier();
        var harness = new LoopBarrierEngineHarness(
            MakeTwoStepFlow(),
            key => new StepResult { Key = key, Status = StepStatus.Succeeded },
            eventNotifier: notifier);

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("first");
        await harness.Store.MarkTimedOutAsync(runId, "Run timed out before scheduling next step.");
        Assert.True((await harness.Store.GetRunControlAsync(runId))!.CancelRequested);

        // Act
        await harness.DrainAsync();
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("TimedOut", await harness.RunStatusAsync(runId));
        Assert.Equal("TimedOut", CompletedStatus(notifier));
    }

    [Fact]
    public async Task GenuineUserCancel_onARunWhoseDeadlineAlsoLapsed_reportsCancelledNotTimedOut()
    {
        // Arrange - the mirror image: a real user cancel must win over a merely lapsed deadline, and
        // must not latch the timeout (which would let a later retry clear the cancel).
        var notifier = new RecordingNotifier();
        var harness = new LoopBarrierEngineHarness(
            MakeTwoStepFlow(),
            key => new StepResult { Key = key, Status = StepStatus.Succeeded },
            eventNotifier: notifier);

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("first");
        await harness.Store.RequestCancelAsync(runId, "user requested");
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await harness.DrainAsync();

        // Assert
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
        Assert.Equal("Run is Cancelled.", await harness.StepReasonAsync(runId, "second"));
        Assert.Equal("Cancelled", CompletedStatus(notifier));
        Assert.Null((await harness.Store.GetRunControlAsync(runId))!.TimedOutAtUtc);
    }
}
