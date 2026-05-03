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

    private FlowOrchestratorEngine CreateEngine(IFlowEventNotifier notifier) =>
        new FlowOrchestratorEngine(
            _dispatcher, _flowExecutor, _graphPlanner, _stepExecutor,
            _flowStore, _runStore, _outputsRepo, _ctxAccessor, _flowRepo,
            [], [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
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
}
