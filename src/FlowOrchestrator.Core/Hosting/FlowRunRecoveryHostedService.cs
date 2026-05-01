using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting;

/// <summary>
/// Hosted service that re-enqueues stuck flow runs on application startup.
/// </summary>
/// <remarks>
/// <para>
/// Runs once during <see cref="StartAsync"/>. For each active (Running) run, evaluates the DAG
/// and re-dispatches any ready step whose dispatch record is missing — indicating that the previous
/// host crashed between persisting a result and enqueuing the next step.
/// </para>
/// <para>
/// Two layers of safety prevent duplicate execution even when multiple replicas start simultaneously:
/// <list type="number">
///   <item><see cref="IFlowRunStore.TryRecordDispatchAsync"/> — atomic INSERT that only one replica wins.</item>
///   <item><see cref="IFlowRunRuntimeStore.TryClaimStepAsync"/> — atomic claim that only one worker executes.</item>
/// </list>
/// </para>
/// <para>
/// Requires <see cref="IFlowRunRuntimeStore"/> to evaluate step statuses.
/// When no runtime store is registered (legacy sequential mode) recovery is skipped.
/// </para>
/// </remarks>
public sealed class FlowRunRecoveryHostedService : IHostedService
{
    private readonly IFlowRunStore _runStore;
    private readonly IFlowRunRuntimeStore? _runtimeStore;
    private readonly IFlowRepository _flowRepository;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly IStepDispatcher _dispatcher;
    private readonly IOutputsRepository _outputsRepository;
    private readonly ILogger<FlowRunRecoveryHostedService> _logger;

    /// <summary>Initialises the recovery service with its dependencies.</summary>
    public FlowRunRecoveryHostedService(
        IFlowRunStore runStore,
        IEnumerable<IFlowRunRuntimeStore> runtimeStores,
        IFlowRepository flowRepository,
        IFlowGraphPlanner graphPlanner,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository,
        ILogger<FlowRunRecoveryHostedService> logger)
    {
        _runStore = runStore;
        _runtimeStore = runtimeStores.FirstOrDefault();
        _flowRepository = flowRepository;
        _graphPlanner = graphPlanner;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
        _logger = logger;
    }

    /// <summary>Scans active runs and re-dispatches any orphaned steps.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runtimeStore is null)
        {
            _logger.LogDebug(
                "FlowRunRecoveryHostedService: no IFlowRunRuntimeStore registered — recovery skipped in legacy sequential mode.");
            return;
        }

        IReadOnlyList<FlowRunRecord> activeRuns;
        try
        {
            activeRuns = await _runStore.GetActiveRunsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlowRunRecoveryHostedService: could not query active runs — recovery skipped.");
            return;
        }

        if (activeRuns.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "FlowRunRecoveryHostedService: found {Count} active run(s) to evaluate for recovery.", activeRuns.Count);

        var allFlows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flowMap = allFlows.ToDictionary(f => f.Id);

        foreach (var run in activeRuns)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!flowMap.TryGetValue(run.FlowId, out var flow))
            {
                _logger.LogWarning(
                    "FlowRunRecoveryHostedService: run {RunId} references unknown flow {FlowId} — skipping.",
                    run.Id, run.FlowId);
                continue;
            }

            try
            {
                await RecoverRunAsync(run, flow, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FlowRunRecoveryHostedService: error recovering run {RunId} — continuing with next run.", run.Id);
            }
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RecoverRunAsync(FlowRunRecord run, IFlowDefinition flow, CancellationToken ct)
    {
        var statuses = await _runtimeStore!.GetStepStatusesAsync(run.Id).ConfigureAwait(false);
        var dispatched = await _runStore.GetDispatchedStepKeysAsync(run.Id).ConfigureAwait(false);
        var evaluation = _graphPlanner.Evaluate(flow, statuses);

        var ctx = await BuildContextAsync(run).ConfigureAwait(false);
        var recovered = 0;

        // 1. Ready steps — re-enqueue if no dispatch record (crash between persist and enqueue).
        foreach (var stepKey in evaluation.ReadyStepKeys)
        {
            if (dispatched.Contains(stepKey))
            {
                continue;  // already in the runtime queue; the worker will pick it up
            }

            var metadata = flow.Manifest.Steps.FindStep(stepKey);
            if (metadata is null)
            {
                continue;
            }

            var step = new StepInstance(stepKey, metadata.Type)
            {
                RunId = run.Id,
                ScheduledTime = DateTimeOffset.UtcNow,
                Inputs = new Dictionary<string, object?>(metadata.Inputs)
            };

            if (await TryDispatchAsync(ctx, flow, step, delay: null, ct).ConfigureAwait(false))
            {
                recovered++;
                _logger.LogInformation(
                    "FlowRunRecoveryHostedService: re-dispatched ready step '{StepKey}' for run {RunId}.",
                    stepKey, run.Id);
            }
        }

        // 2. Waiting (pending/polling) steps — re-schedule if no dispatch record.
        foreach (var stepKey in evaluation.WaitingStepKeys)
        {
            if (dispatched.Contains(stepKey))
            {
                continue;  // poll already scheduled; it will fire at its scheduled time
            }

            var metadata = flow.Manifest.Steps.FindStep(stepKey);
            if (metadata is null)
            {
                continue;
            }

            // Use a short fixed delay on recovery rather than trying to reconstruct the original poll interval.
            var recoveryDelay = TimeSpan.FromSeconds(10);

            var step = new StepInstance(stepKey, metadata.Type)
            {
                RunId = run.Id,
                ScheduledTime = DateTimeOffset.UtcNow + recoveryDelay,
                Inputs = new Dictionary<string, object?>(metadata.Inputs)
            };

            if (await TryDispatchAsync(ctx, flow, step, recoveryDelay, ct).ConfigureAwait(false))
            {
                recovered++;
                _logger.LogInformation(
                    "FlowRunRecoveryHostedService: re-scheduled waiting step '{StepKey}' for run {RunId} with {Delay}s delay.",
                    stepKey, run.Id, recoveryDelay.TotalSeconds);
            }
        }

        if (recovered > 0)
        {
            return;
        }

        // Zombie detection: nothing left to dispatch AND nothing in-flight AND nothing
        // is waiting on a dependency. The run reached a terminal state but the engine's
        // continuation never closed it (e.g., the host crashed between persisting the
        // last step result and calling CompleteRunAsync, or an earlier deserialisation
        // failure prevented continuation). We close it here using the same termination
        // rules the engine uses inline.
        var anyInFlight = statuses.Values.Any(s => s is StepStatus.Running or StepStatus.Pending);
        if (anyInFlight
            || evaluation.ReadyStepKeys.Count > 0
            || evaluation.WaitingStepKeys.Count > 0
            || statuses.Count == 0)
        {
            _logger.LogDebug(
                "FlowRunRecoveryHostedService: run {RunId} — no orphaned steps detected.", run.Id);
            return;
        }

        var terminalStatus = ComputeTerminalStatus(flow, statuses);
        try
        {
            await _runStore.CompleteRunAsync(run.Id, terminalStatus).ConfigureAwait(false);
            _logger.LogInformation(
                "FlowRunRecoveryHostedService: closed zombie run {RunId} as {Status} (all steps terminal but run remained Running).",
                run.Id, terminalStatus);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "FlowRunRecoveryHostedService: failed to close zombie run {RunId}.", run.Id);
        }
    }

    /// <summary>
    /// Computes the terminal status of a run from its step statuses, mirroring the rules
    /// the engine applies inline at the end of <c>RunGraphContinuationAsync</c>.
    /// </summary>
    private static string ComputeTerminalStatus(IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var anySucceeded = statuses.Values.Any(x => x == StepStatus.Succeeded);

        if (!anySucceeded)
        {
            var anyFailed = statuses.Values.Any(x => x == StepStatus.Failed);
            var anySkipped = statuses.Values.Any(x => x == StepStatus.Skipped);
            return anyFailed
                ? StepStatus.Failed.ToString()
                : anySkipped
                    ? StepStatus.Skipped.ToString()
                    : StepStatus.Failed.ToString();
        }

        var hasUnhandledFailure = statuses.Any(kvp =>
            kvp.Value == StepStatus.Failed &&
            !flow.Manifest.Steps.Any(other =>
                other.Value.RunAfter?.ContainsKey(kvp.Key) == true &&
                statuses.TryGetValue(other.Key, out var s) &&
                s == StepStatus.Succeeded));

        if (hasUnhandledFailure)
        {
            return StepStatus.Failed.ToString();
        }

        var leafKeys = statuses.Keys
            .Where(k => !flow.Manifest.Steps.Any(kvp =>
                kvp.Value.RunAfter?.ContainsKey(k) == true))
            .ToHashSet(StringComparer.Ordinal);

        var allLeavesSkipped = leafKeys.Count > 0 &&
            leafKeys.All(k => statuses.TryGetValue(k, out var s) && s == StepStatus.Skipped);

        return allLeavesSkipped
            ? StepStatus.Skipped.ToString()
            : StepStatus.Succeeded.ToString();
    }

    /// <summary>
    /// Atomically records the dispatch and hands off to the runtime adapter.
    /// Returns <see langword="true"/> if this recovery instance won the dispatch race.
    /// </summary>
    private async Task<bool> TryDispatchAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance step,
        TimeSpan? delay,
        CancellationToken ct)
    {
        // Idempotent guard — only one replica wins this INSERT.
        if (!await _runStore.TryRecordDispatchAsync(ctx.RunId, step.Key, ct).ConfigureAwait(false))
        {
            return false;
        }

        string? jobId;
        if (delay.HasValue)
        {
            jobId = await _dispatcher.ScheduleStepAsync(ctx, flow, step, delay.Value, ct).ConfigureAwait(false);
        }
        else
        {
            jobId = await _dispatcher.EnqueueStepAsync(ctx, flow, step, ct).ConfigureAwait(false);
        }

        if (jobId is not null)
        {
            try
            {
                await _runStore.AnnotateDispatchAsync(ctx.RunId, step.Key, jobId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FlowRunRecoveryHostedService: failed to annotate dispatch for step '{StepKey}'.", step.Key);
            }
        }

        return true;
    }

    private async Task<IExecutionContext> BuildContextAsync(FlowRunRecord run)
    {
        var ctx = new Execution.ExecutionContext { RunId = run.Id };
        try
        {
            ctx.TriggerData = await _outputsRepository.GetTriggerDataAsync(run.Id).ConfigureAwait(false);
            ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(run.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "FlowRunRecoveryHostedService: could not restore trigger data for run {RunId}.", run.Id);
        }
        return ctx;
    }
}
