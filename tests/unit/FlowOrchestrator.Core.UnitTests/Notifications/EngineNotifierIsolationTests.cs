using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Notifications;

/// <summary>
/// Pins the contract that <see cref="IFlowEventNotifier"/> failures MUST NOT abort flow execution:
/// the engine's <c>PublishEventSafelyAsync</c> wrapper must catch every exception and log instead.
/// A misbehaving realtime sink (slow network, disposed broadcaster) must never break a run.
/// </summary>
public sealed class EngineNotifierIsolationTests
{
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowStore _flowStore = Substitute.For<IFlowStore>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger = Substitute.For<ILogger<FlowOrchestratorEngine>>();

    private FlowOrchestratorEngine CreateEngine(IFlowEventNotifier notifier, bool enableEventPersistence = false) =>
        new FlowOrchestratorEngine(
            _dispatcher, _flowExecutor, _graphPlanner, _stepExecutor,
            _flowStore, _runStore, _outputsRepo, _ctxAccessor, _flowRepo,
            [], [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = enableEventPersistence, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger,
            notifier);

    private static IFlowDefinition MakeFlow(string stepKey)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var steps = new StepCollection
        {
            [stepKey] = new StepMetadata { Type = "Work" }
        };
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private static ITriggerContext MakeTriggerCtx(IFlowDefinition flow) =>
        new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

    [Fact]
    public async Task TriggerAsync_when_notifier_throws_run_still_completes_via_dispatcher()
    {
        // Arrange — every PublishAsync throws. Engine must continue and dispatch the entry step.
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));

        var notifier = Substitute.For<IFlowEventNotifier>();
        notifier.WhenForAnyArgs(n => n.PublishAsync(default!, default))
            .Do(_ => throw new InvalidOperationException("simulated sink failure"));

        var flow = MakeFlow("step1");
        var ctx = MakeTriggerCtx(flow);

        // Act + Assert — must not throw.
        var result = await CreateEngine(notifier).TriggerAsync(ctx);

        // Assert — engine reached the dispatch site despite notifier explosions.
        Assert.NotNull(result);
        await _dispatcher.ReceivedWithAnyArgs().EnqueueStepAsync(default!, default!, default!);
    }

    [Fact]
    public async Task TriggerAsync_invokes_notifier_with_RunStartedEvent()
    {
        // Arrange
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));

        var notifier = Substitute.For<IFlowEventNotifier>();
        var flow = MakeFlow("step1");
        var ctx = MakeTriggerCtx(flow);

        // Act
        await CreateEngine(notifier).TriggerAsync(ctx);

        // Assert
        await notifier.Received(1).PublishAsync(
            Arg.Is<FlowLifecycleEvent>(e =>
                ((RunStartedEvent)e).RunId == ctx.RunId &&
                ((RunStartedEvent)e).FlowId == flow.Id &&
                ((RunStartedEvent)e).TriggerKey == "manual"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_when_notifier_throws_TaskCanceledException_run_still_completes()
    {
        // Arrange — TaskCanceledException is a subtype of OperationCanceledException.
        // Regression guard for a bug introduced by the CodeQL "cs/catch-of-all-exceptions" sweep,
        // which initially rewrote PublishEventSafelyAsync's catch from `catch (Exception)` to
        // `when (ex is not OperationCanceledException)`. That refactor accidentally weakened the
        // documented isolation contract ("Telemetry must NEVER abort a flow"). After the fix the
        // filter is `when (ex is not null)` — a CodeQL-quiet tautology that preserves the original
        // semantics. This test will fail again if anyone re-tightens the filter without revisiting
        // the XML doc.
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));

        var notifier = Substitute.For<IFlowEventNotifier>();
        notifier.WhenForAnyArgs(n => n.PublishAsync(default!, default))
            .Do(_ => throw new TaskCanceledException("notifier-internal cancellation"));

        var flow = MakeFlow("step1");
        var ctx = MakeTriggerCtx(flow);

        // Act — must not throw an unhandled exception out of TriggerAsync. If the engine's
        // outer notifier-failure isolation regressed, this would throw TaskCanceledException.
        var result = await CreateEngine(notifier).TriggerAsync(ctx);

        // Assert — engine completed the trigger handshake despite the OCE-family throw.
        Assert.NotNull(result);
    }

    [Fact]
    public async Task TriggerAsync_when_notifier_throws_OperationCanceledException_run_still_completes()
    {
        // Arrange — same root issue as the TaskCanceledException test above but for the bare OCE
        // type. A notifier implementation that maps a downstream cancellation (Channel writer closed,
        // backplane circuit-breaker) to OCE must still NOT abort the engine.
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));

        var notifier = Substitute.For<IFlowEventNotifier>();
        notifier.WhenForAnyArgs(n => n.PublishAsync(default!, default))
            .Do(_ => throw new OperationCanceledException("notifier internal cancellation"));

        var flow = MakeFlow("step1");
        var ctx = MakeTriggerCtx(flow);

        // Act
        var result = await CreateEngine(notifier).TriggerAsync(ctx);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task TriggerAsync_when_outputs_repository_RecordEventAsync_throws_OCE_run_still_completes()
    {
        // Arrange — same regression class as the notifier OCE test, but on the engine's other
        // best-effort observability path: IOutputsRepository.RecordEventAsync. The pre-fix filter
        // `when (ex is not OperationCanceledException)` weakened this from the documented
        // "best-effort, MUST NEVER abort a flow" contract; this test pins the restored behaviour.
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));

        // Make the OutputsRepository throw OCE on every RecordEventAsync call.
        _outputsRepo.WhenForAnyArgs(r => r.RecordEventAsync(default!, default!, default!, default!))
            .Do(_ => throw new OperationCanceledException("repository internal cancellation"));

        var notifier = Substitute.For<IFlowEventNotifier>();
        var flow = MakeFlow("step1");
        var ctx = MakeTriggerCtx(flow);

        // Act — must not throw even though the event-persistence layer is broken.
        var result = await CreateEngine(notifier, enableEventPersistence: true).TriggerAsync(ctx);

        // Assert
        Assert.NotNull(result);
        // Engine still dispatched the entry step despite repository OCE.
        await _dispatcher.ReceivedWithAnyArgs().EnqueueStepAsync(default!, default!, default!);
    }

    [Fact]
    public async Task TriggerAsync_when_notifier_throws_object_disposed_exception_run_still_completes()
    {
        // Arrange — ObjectDisposedException is the classic "broadcaster is shutting down" failure
        // mode that a real notifier can hit when an event lands after the host's StopAsync. Even
        // under the broken pre-fix filter this case worked (ODE is not OCE), but the test pins
        // the broader "telemetry must NEVER abort a flow" contract so any future weakening across
        // exception families regresses loudly.
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));

        var notifier = Substitute.For<IFlowEventNotifier>();
        notifier.WhenForAnyArgs(n => n.PublishAsync(default!, default))
            .Do(_ => throw new ObjectDisposedException("broadcaster", "Sink already disposed."));

        var flow = MakeFlow("step1");
        var ctx = MakeTriggerCtx(flow);

        // Act
        var result = await CreateEngine(notifier).TriggerAsync(ctx);

        // Assert
        Assert.NotNull(result);
        await _dispatcher.ReceivedWithAnyArgs().EnqueueStepAsync(default!, default!, default!);
    }
}
