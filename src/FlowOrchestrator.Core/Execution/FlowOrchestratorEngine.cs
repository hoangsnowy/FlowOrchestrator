using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Runtime-neutral implementation of <see cref="IFlowOrchestrator"/>.
/// Contains the full flow execution lifecycle — trigger, step execution, graph continuation,
/// polling, retry, and cancellation — with zero runtime-specific dependencies.
/// Hangfire, in-memory channels, and queue consumers all delegate to this engine.
/// </summary>
/// <remarks>
/// <para>
/// After every step completion the engine evaluates the full DAG, enabling parallel fan-out, loop
/// steps, and skip propagation. That evaluation, and the claim guard behind "dispatch many, execute
/// once", require an <see cref="IFlowRunRuntimeStore"/> and an <see cref="IFlowRunControlStore"/>;
/// construction fails when the storage backend registers neither.
/// </para>
/// <para>
/// The implementation is split across five partial files for readability — each handles
/// one responsibility phase: ctor + helpers (this file), trigger lifecycle
/// (<c>FlowOrchestratorEngine.Trigger.cs</c>), per-step execution
/// (<c>FlowOrchestratorEngine.Step.cs</c>), DAG continuation
/// (<c>FlowOrchestratorEngine.Continuation.cs</c>), and dispatch / control / event plumbing
/// (<c>FlowOrchestratorEngine.Control.cs</c>).
/// </para>
/// </remarks>
public sealed partial class FlowOrchestratorEngine : IFlowOrchestrator, IRunTimeoutEnforcer
{
    private readonly IStepDispatcher _dispatcher;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly IStepExecutor _stepExecutor;
    private readonly IFlowStore _flowStore;
    private readonly IFlowRunStore _runStore;
    private readonly IOutputsRepository _outputsRepository;
    private readonly IExecutionContextAccessor _contextAccessor;
    private readonly FlowRegistry _flows;
    private readonly IFlowRunRuntimeStore _runtimeStore;
    private readonly IFlowRunControlStore _runControlStore;
    private readonly FlowRunControlOptions _runControlOptions;
    private readonly FlowObservabilityOptions _observabilityOptions;
    private readonly FlowOrchestratorTelemetry _telemetry;
    private readonly ILogger<FlowOrchestratorEngine> _logger;
    private readonly WhenClauseEvaluator _whenEvaluator;
    private readonly IFlowEventNotifier _eventNotifier;
    private readonly IFlowSignalStore? _signalStore;
    private readonly StepLeaseKeeper? _leaseKeeper;
    private readonly string? _serverId;

    /// <summary>Initialises the engine with all required and optional dependencies.</summary>
    /// <remarks>
    /// <paramref name="eventNotifier"/> is required: <c>AddFlowOrchestrator</c> registers
    /// <see cref="NoopFlowEventNotifier"/> by default, and hand-built engines pass
    /// <see cref="NoopFlowEventNotifier.Instance"/> when lifecycle events should be discarded.
    /// <para>
    /// <paramref name="signalStore"/> is optional so hand-built engines without signal support compile
    /// unchanged. When supplied, a step that parks in <see cref="StepStatus.Pending"/> re-reads its
    /// signal waiter after releasing its execution claim and resumes itself if a payload landed while
    /// it held the claim — the delivery whose resume nudge could only lose that claim (#190). Without
    /// it, such a delivery waits for the step's safety-net invocation.
    /// </para>
    /// <para>
    /// <paramref name="leaseKeeper"/> turns on step execution leases (<see cref="FlowRunControlOptions.StepLeaseDuration"/>):
    /// a step is claimed under a lease the keeper renews while its handler runs, and a lease whose worker died is
    /// taken over by the next delivery. <c>AddFlowOrchestrator</c> registers it; without it (a hand-built engine),
    /// claims never expire, as in 1.x.
    /// </para>
    /// </remarks>
    public FlowOrchestratorEngine(
        IStepDispatcher dispatcher,
        IFlowGraphPlanner graphPlanner,
        IStepExecutor stepExecutor,
        IFlowStore flowStore,
        IFlowRunStore runStore,
        IOutputsRepository outputsRepository,
        IExecutionContextAccessor contextAccessor,
        FlowRegistry flows,
        IEnumerable<IFlowRunRuntimeStore> runtimeStores,
        IEnumerable<IFlowRunControlStore> runControlStores,
        FlowRunControlOptions runControlOptions,
        FlowObservabilityOptions observabilityOptions,
        FlowOrchestratorTelemetry telemetry,
        ILogger<FlowOrchestratorEngine> logger,
        IFlowEventNotifier eventNotifier,
        IFlowSignalStore? signalStore = null,
        StepLeaseKeeper? leaseKeeper = null)
    {
        _dispatcher = dispatcher;
        _graphPlanner = graphPlanner;
        _stepExecutor = stepExecutor;
        _flowStore = flowStore;
        _runStore = runStore;
        _outputsRepository = outputsRepository;
        _contextAccessor = contextAccessor;
        _flows = flows;
        _runtimeStore = RequiredStore.First(runtimeStores, nameof(FlowOrchestratorEngine));
        _runControlStore = RequiredStore.First(runControlStores, nameof(FlowOrchestratorEngine));
        _runControlOptions = runControlOptions;
        _observabilityOptions = observabilityOptions;
        _telemetry = telemetry;
        _logger = logger;
        _eventNotifier = eventNotifier;
        _signalStore = signalStore;
        _leaseKeeper = leaseKeeper is { Duration: not null } ? leaseKeeper : null;
        // Stamped on every attempt this process starts, leases on or off: maps the attempt to its server.
        _serverId = leaseKeeper?.OwnerId;
        _whenEvaluator = new WhenClauseEvaluator(outputsRepository, runStore);
    }

    private static string? SafeSerialize(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Undefined ? null : element.GetRawText();
        return JsonSerializer.Serialize(value);
    }

    private static bool IsInFlight(StepStatus status) =>
        status is StepStatus.Running or StepStatus.Pending;

    private async ValueTask EnsureTriggerDataAsync(IExecutionContext ctx)
    {
        var fromRepo = await _outputsRepository.GetTriggerDataAsync(ctx.RunId).ConfigureAwait(false);
        if (fromRepo is not null)
        {
            ctx.TriggerData = fromRepo;
        }
        else if (ctx.TriggerData is null && ctx is ITriggerContext triggerContext && triggerContext.Trigger is not null)
        {
            ctx.TriggerData = triggerContext.Trigger.Data;
        }

        if (ctx.TriggerHeaders is null)
        {
            ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(ctx.RunId).ConfigureAwait(false);
        }
    }

    private static IStepInstance CreateRunLevelStep(Guid runId)
    {
        return new StepInstance(RunLevelStepKey, "Run")
        {
            RunId = runId,
            ScheduledTime = DateTimeOffset.UtcNow
        };
    }
}
