using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
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
/// Supports two continuation modes:
/// <list type="bullet">
///   <item><b>Graph mode</b> (default when <see cref="IFlowRunRuntimeStore"/> is registered):
///     evaluates the full DAG after each step, enabling parallel fan-out, loop steps, and skip propagation.</item>
///   <item><b>Legacy sequential mode</b>: simple linear next-step resolution when no runtime store is available.</item>
/// </list>
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
public sealed partial class FlowOrchestratorEngine : IFlowOrchestrator
{
    private readonly IStepDispatcher _dispatcher;
    private readonly IFlowExecutor _flowExecutor;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly IStepExecutor _stepExecutor;
    private readonly IFlowStore _flowStore;
    private readonly IFlowRunStore _runStore;
    private readonly IOutputsRepository _outputsRepository;
    private readonly IExecutionContextAccessor _contextAccessor;
    private readonly IFlowRepository _flowRepository;
    private readonly IFlowRunRuntimeStore? _runtimeStore;
    private readonly IFlowRunControlStore? _runControlStore;
    private readonly FlowRunControlOptions _runControlOptions;
    private readonly FlowObservabilityOptions _observabilityOptions;
    private readonly FlowOrchestratorTelemetry _telemetry;
    private readonly ILogger<FlowOrchestratorEngine> _logger;
    private readonly WhenClauseEvaluator _whenEvaluator;
    private readonly IFlowEventNotifier _eventNotifier;

    /// <summary>Initialises the engine with all required and optional dependencies.</summary>
    /// <remarks>
    /// <paramref name="eventNotifier"/> is optional with a default of <see langword="null"/> so
    /// existing positional callers (notably unit tests built before the realtime layer landed)
    /// continue to compile unchanged. When <see langword="null"/>, <see cref="NoopFlowEventNotifier.Instance"/>
    /// is substituted and lifecycle events are silently discarded.
    /// </remarks>
    public FlowOrchestratorEngine(
        IStepDispatcher dispatcher,
        IFlowExecutor flowExecutor,
        IFlowGraphPlanner graphPlanner,
        IStepExecutor stepExecutor,
        IFlowStore flowStore,
        IFlowRunStore runStore,
        IOutputsRepository outputsRepository,
        IExecutionContextAccessor contextAccessor,
        IFlowRepository flowRepository,
        IEnumerable<IFlowRunRuntimeStore> runtimeStores,
        IEnumerable<IFlowRunControlStore> runControlStores,
        FlowRunControlOptions runControlOptions,
        FlowObservabilityOptions observabilityOptions,
        FlowOrchestratorTelemetry telemetry,
        ILogger<FlowOrchestratorEngine> logger,
        IFlowEventNotifier? eventNotifier = null)
    {
        _dispatcher = dispatcher;
        _flowExecutor = flowExecutor;
        _graphPlanner = graphPlanner;
        _stepExecutor = stepExecutor;
        _flowStore = flowStore;
        _runStore = runStore;
        _outputsRepository = outputsRepository;
        _contextAccessor = contextAccessor;
        _flowRepository = flowRepository;
        _runtimeStore = runtimeStores.FirstOrDefault();
        _runControlStore = runControlStores.FirstOrDefault();
        _runControlOptions = runControlOptions;
        _observabilityOptions = observabilityOptions;
        _telemetry = telemetry;
        _logger = logger;
        _eventNotifier = eventNotifier ?? NoopFlowEventNotifier.Instance;
        _whenEvaluator = new WhenClauseEvaluator(outputsRepository, runStore);

        if (_runtimeStore is null)
        {
            EngineLog.LegacySequentialMode(_logger);
        }
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

    private static IStepInstance CreateRunEventStep(Guid runId)
    {
        return new StepInstance("__run", "Run")
        {
            RunId = runId,
            ScheduledTime = DateTimeOffset.UtcNow
        };
    }
}
