using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Pins the ordering of the engine's <see cref="StepStatus.Pending"/> path against the two claim
/// races described in issue #190, and the event-type mapping for non-terminal results.
/// </summary>
/// <remarks>
/// Every storage and dispatcher call the Pending path makes is appended to one shared log, so each
/// test asserts on the sequence rather than on individual <c>Received()</c> counts. The sequence is
/// the contract: a signal resume observes the world between any two of these calls, and the step
/// must never be simultaneously unclaimed and missing its dispatch-ledger row.
/// </remarks>
public sealed class PendingPathClaimOrderingTests
{
    private const string StepKey = "wait_approval";

    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IFlowRunRuntimeStore _runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger = Substitute.For<ILogger<FlowOrchestratorEngine>>();
    private readonly List<string> _calls = [];
    private readonly Guid _runId = Guid.NewGuid();

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
                DelayNextStep = TimeSpan.FromMinutes(5)
            }));
    }

    [Fact]
    public async Task Pending_reserves_the_dispatch_ledger_before_releasing_the_claim_and_dispatches_after()
    {
        // Arrange
        var engine = CreateEngine();

        // Act
        await RunStepAsync(engine);

        // Assert — the claim is released only once the next attempt's ledger row exists, and the
        // runtime only sees the attempt after the claim is gone (a fast dispatcher firing before the
        // release would lose the claim itself).
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

        // Assert — emitted before the release, so a signal resume that claims and completes the step
        // the instant the claim clears can never appear before it in the timeline.
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
    public async Task Pending_does_not_dispatch_when_another_party_reserved_the_ledger_row_first()
    {
        // Arrange — someone recorded a dispatch row inside the release/record gap; that party owns
        // the next attempt, so enqueuing a second one would double-dispatch the step.
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("record-dispatch");
                return Task.FromResult(false);
            });
        var engine = CreateEngine();

        // Act
        await RunStepAsync(engine);

        // Assert — the claim is still released so the owning attempt can claim it, and the skipped
        // reschedule leaves a trace (#186: a re-poll that never arrived left nothing in the logs).
        Assert.DoesNotContain("schedule", _calls);
        Assert.Contains("release-claim", _calls);
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
        var engine = CreateEngine();

        // Act
        var ex = await Record.ExceptionAsync(() => RunStepAsync(engine));

        // Assert — the reschedule failure propagates (so the runtime retries the job) and the ledger
        // is re-asserted after it, so the step is never both unclaimed and unreserved.
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal("record-dispatch", _calls[^1]);
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
        var engine = CreateEngine(telemetry, enableOpenTelemetry: true);

        // Act
        await RunStepAsync(engine);

        // Assert
        Assert.Equal([(1L, StepKey)], measured);
        Assert.Empty(_stepExecutor.ReceivedCalls());
    }

    private async Task RunStepAsync(FlowOrchestratorEngine engine)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { [StepKey] = new StepMetadata { Type = "WaitForSignal" } }
        });

        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = _runId },
            flow,
            new StepInstance(StepKey, "WaitForSignal") { RunId = _runId });
    }

    private FlowOrchestratorEngine CreateEngine(FlowOrchestratorTelemetry? telemetry = null, bool enableOpenTelemetry = false) =>
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
            [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = true, EnableOpenTelemetry = enableOpenTelemetry },
            telemetry ?? new FlowOrchestratorTelemetry(),
            _logger);
}
