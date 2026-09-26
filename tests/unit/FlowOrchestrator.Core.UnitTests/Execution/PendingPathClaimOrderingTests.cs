using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Pins the engine's <see cref="StepStatus.Pending"/> path against the two claim races of issue #190:
/// the order of the ledger / claim / dispatch hand-off, the post-release signal-waiter re-check, and
/// the event-type mapping for non-terminal results.
/// </summary>
/// <remarks>
/// Every storage and dispatcher call the Pending path makes is appended to one shared log, so the
/// ordering tests assert on the sequence rather than on individual <c>Received()</c> counts. The
/// sequence is the contract: a signal delivery observes the world between any two of these calls.
/// </remarks>
public sealed class PendingPathClaimOrderingTests
{
    private const string StepKey = "wait_approval";

    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IFlowRunRuntimeStore _runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
    private readonly IFlowSignalStore _signalStore = Substitute.For<IFlowSignalStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger = Substitute.For<ILogger<FlowOrchestratorEngine>>();
    private readonly List<string> _calls = [];
    private readonly Guid _runId = Guid.NewGuid();
    private IStepInstance? _parkedInstance;

    /// <summary>Wires every substitute the Pending path touches to record into <see cref="_calls"/>.</summary>
    public PendingPathClaimOrderingTests()
    {
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _runtimeStore.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));
        _runtimeStore.ReleaseStepClaimAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(_ =>
            {
                _calls.Add("release-claim");
                return Task.CompletedTask;
            });

        _runStore.ReleaseDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("release-dispatch");
                return Task.CompletedTask;
            });
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("record-dispatch");
                return Task.FromResult(true);
            });

        _dispatcher.ScheduleStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("schedule");
                return new ValueTask<string?>("job-1");
            });
        _dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("enqueue");
                return new ValueTask<string?>("job-2");
            });

        _signalStore.GetWaiterAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("read-waiter");
                return new ValueTask<FlowSignalWaiter?>((FlowSignalWaiter?)null);
            });

        _outputsRepo.RecordEventAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<FlowEvent>())
            .Returns(call =>
            {
                _calls.Add("event:" + call.Arg<FlowEvent>().Type);
                return ValueTask.CompletedTask;
            });

        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(new ValueTask<IStepResult>(new StepResult
            {
                Key = StepKey,
                Status = StepStatus.Pending,
                DelayNextStep = TimeSpan.FromHours(24)
            }));
    }

    // ── Race 2: ledger / claim / dispatch hand-off ──────────────────────────────

    [Fact]
    public async Task Pending_reserves_the_dispatch_ledger_before_releasing_the_claim_and_dispatches_after()
    {
        // Arrange
        var engine = CreateEngine();

        // Act
        await RunStepAsync(engine);

        // Assert — the step is never both unclaimed and unreserved, and the runtime only sees the next
        // attempt once the claim is gone (a fast dispatcher firing before the release would lose it).
        var pendingTail = _calls.SkipWhile(c => c != "release-dispatch").ToList();
        Assert.Equal(["release-dispatch", "record-dispatch", "release-claim", "schedule"], pendingTail);
    }

    [Fact]
    public async Task Pending_emits_step_pending_while_the_claim_is_still_held()
    {
        // Arrange
        var engine = CreateEngine();

        // Act
        await RunStepAsync(engine);

        // Assert — a resume that claims and completes the step the instant the claim clears can
        // never appear before this event in the timeline.
        var pendingIndex = _calls.IndexOf("event:step.pending");
        var releaseIndex = _calls.IndexOf("release-claim");
        Assert.NotEqual(-1, pendingIndex);
        Assert.True(pendingIndex < releaseIndex, string.Join(", ", _calls));
    }

    [Fact]
    public async Task Pending_result_never_emits_step_completed()
    {
        // Arrange
        var engine = CreateEngine();

        // Act
        await RunStepAsync(engine);

        // Assert — pre-#190 a Pending result fell into the default arm of the event switch and logged
        // "step.completed … with status Pending" right before the real step.pending.
        Assert.DoesNotContain("event:step.completed", _calls);
        Assert.Single(_calls, c => c == "event:step.pending");
    }

    [Fact]
    public async Task Pending_still_dispatches_when_another_party_recorded_the_ledger_row_first()
    {
        // Arrange — recovery or a retry recorded the row inside the release/record gap and its job
        // already ran and lost the claim this invocation holds. Skipping the reschedule would leave
        // the step unclaimed with nothing queued, and recovery skips keys that carry a dispatch row.
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("record-dispatch");
                return Task.FromResult(false);
            });
        var engine = CreateEngine();

        // Act
        await RunStepAsync(engine);

        // Assert — a duplicate is absorbed by the claim; a missing attempt strands the step.
        var pendingTail = _calls.SkipWhile(c => c != "release-dispatch").ToList();
        Assert.Equal(["release-dispatch", "record-dispatch", "release-claim", "schedule"], pendingTail);
        Assert.Contains(_logger.ReceivedCalls(), c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log)
            && c.GetArguments()[1] is EventId { Id: 3007 });
    }

    [Fact]
    public async Task Pending_reasserts_the_ledger_when_the_dispatcher_throws_after_the_claim_release()
    {
        // Arrange
        _dispatcher.ScheduleStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => throw new InvalidOperationException("broker down"));
        var engine = CreateEngine(withSignalStore: true);

        // Act
        var ex = await Record.ExceptionAsync(() => RunStepAsync(engine));

        // Assert — the failure propagates (so the runtime retries the job), the ledger is re-asserted
        // after it, and the signal re-check is not attempted on a reschedule that never happened.
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal("record-dispatch", _calls[^1]);
        Assert.DoesNotContain("read-waiter", _calls);
    }

    // ── Race 1: post-release signal-waiter re-check ─────────────────────────────

    [Fact]
    public async Task Pending_resumes_immediately_when_the_signal_landed_while_the_claim_was_held()
    {
        // Arrange — the delivery committed while this invocation held the claim, so the dispatcher's
        // nudge could only lose it. The post-release re-check must pick the delivery up.
        _signalStore.GetWaiterAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("read-waiter");
                return new ValueTask<FlowSignalWaiter?>(DeliveredWaiter());
            });
        IStepInstance? resumed = null;
        CancellationToken resumeToken = default;
        _dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
                Arg.Do<IStepInstance>(s => resumed = s), Arg.Do<CancellationToken>(t => resumeToken = t))
            .Returns(_ =>
            {
                _calls.Add("enqueue");
                return new ValueTask<string?>("job-2");
            });
        var engine = CreateEngine(withSignalStore: true);
        var before = DateTimeOffset.UtcNow;

        // Act
        await RunStepAsync(engine);

        // Assert — the waiter is read only after the claim is released (so it observes any delivery
        // that saw the claim held), and the resume is a fresh instance due now, not the parked one.
        var tail = _calls.SkipWhile(c => c != "release-claim").ToList();
        Assert.Equal(["release-claim", "schedule", "read-waiter", "enqueue"], tail);
        Assert.NotNull(resumed);
        Assert.NotSame(_parkedInstance, resumed);
        Assert.Equal(StepKey, resumed!.Key);
        Assert.InRange(resumed.ScheduledTime, before, DateTimeOffset.UtcNow);
        Assert.Equal(CancellationToken.None, resumeToken);
    }

    [Fact]
    public async Task Pending_does_not_resume_when_the_waiter_has_no_delivery()
    {
        // Arrange — the ordinary park: nothing delivered yet.
        var engine = CreateEngine(withSignalStore: true);

        // Act
        await RunStepAsync(engine);

        // Assert
        Assert.Contains("read-waiter", _calls);
        Assert.DoesNotContain("enqueue", _calls);
    }

    [Fact]
    public async Task Pending_skips_the_re_check_when_no_signal_store_is_registered()
    {
        // Arrange
        var engine = CreateEngine(withSignalStore: false);

        // Act
        await RunStepAsync(engine);

        // Assert
        Assert.DoesNotContain("read-waiter", _calls);
        Assert.DoesNotContain("enqueue", _calls);
    }

    [Fact]
    public async Task A_failing_waiter_re_check_is_logged_and_does_not_fail_the_step()
    {
        // Arrange — the reschedule already succeeded; the re-check is best-effort.
        _signalStore.GetWaiterAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var engine = CreateEngine(withSignalStore: true);

        // Act
        var ex = await Record.ExceptionAsync(() => RunStepAsync(engine));

        // Assert
        Assert.Null(ex);
        Assert.Contains("schedule", _calls);
        Assert.DoesNotContain("enqueue", _calls);
        Assert.Contains(_logger.ReceivedCalls(), c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log)
            && c.GetArguments()[1] is EventId { Id: 3009 });
    }

    // ── Events & telemetry ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_step_skipped_at_the_entry_gate_records_step_skipped()
    {
        // Arrange — the run was cancelled before this attempt started. The terminated-mid-poll path
        // records step.skipped; the entry gate must record the same event so the timeline is complete
        // whichever gate caught the step.
        var controlStore = Substitute.For<IFlowRunControlStore>();
        controlStore.GetRunControlAsync(_runId)
            .Returns(Task.FromResult<FlowRunControlRecord?>(new FlowRunControlRecord { RunId = _runId, CancelRequested = true }));
        var engine = CreateEngine(runControlStore: controlStore);

        // Act
        await RunStepAsync(engine);

        // Assert
        Assert.Contains("event:step.skipped", _calls);
        Assert.Empty(_stepExecutor.ReceivedCalls());
    }

    [Fact]
    public async Task Claim_loss_is_counted_on_the_step_claim_lost_meter()
    {
        // Arrange
        _runtimeStore.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(Task.FromResult(false));
        var telemetry = new FlowOrchestratorTelemetry();
        var measured = new List<(long Value, string? StepKey)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, telemetry.Meter) && instrument.Name == "flow_step_claim_lost")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? stepKey = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "step_key")
                {
                    stepKey = tag.Value as string;
                }
            }

            measured.Add((value, stepKey));
        });
        listener.Start();
        var engine = CreateEngine(telemetry: telemetry, enableOpenTelemetry: true);

        // Act
        await RunStepAsync(engine);

        // Assert
        Assert.Equal([(1L, StepKey)], measured);
        Assert.Empty(_stepExecutor.ReceivedCalls());
    }

    private static FlowSignalWaiter DeliveredWaiter() => new()
    {
        StepKey = StepKey,
        SignalName = "approval",
        CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        DeliveredAt = DateTimeOffset.UtcNow,
        PayloadJson = "{\"approved\":true}"
    };

    private async Task RunStepAsync(FlowOrchestratorEngine engine)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { [StepKey] = new StepMetadata { Type = "WaitForSignal" } }
        });

        _parkedInstance = new StepInstance(StepKey, "WaitForSignal") { RunId = _runId };
        await engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, flow, _parkedInstance);
    }

    private FlowOrchestratorEngine CreateEngine(
        bool withSignalStore = false,
        IFlowRunControlStore? runControlStore = null,
        FlowOrchestratorTelemetry? telemetry = null,
        bool enableOpenTelemetry = false) =>
        new(
            _dispatcher,
            Substitute.For<IFlowExecutor>(),
            new FlowGraphPlanner(),
            _stepExecutor,
            Substitute.For<IFlowStore>(),
            _runStore,
            _outputsRepo,
            Substitute.For<IExecutionContextAccessor>(),
            Substitute.For<IFlowRepository>(),
            [_runtimeStore],
            runControlStore is null ? [] : [runControlStore],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = true, EnableOpenTelemetry = enableOpenTelemetry },
            telemetry ?? new FlowOrchestratorTelemetry(),
            _logger,
            eventNotifier: null,
            signalStore: withSignalStore ? _signalStore : null);
}
