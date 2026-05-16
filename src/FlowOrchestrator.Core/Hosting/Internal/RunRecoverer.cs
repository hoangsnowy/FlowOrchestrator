using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting.Internal;

/// <summary>
/// Worker that recovers a single in-flight flow run on host startup. Collaborator of
/// <see cref="FlowRunRecoveryHostedService"/>; the host iterates active runs and
/// delegates per-run work here.
/// </summary>
/// <remarks>
/// Pulled out of <see cref="FlowRunRecoveryHostedService"/> so the host stays a thin
/// orchestration loop while the per-run state machine (re-dispatch ready / waiting
/// steps, then zombie-detect and close) lives in one focused type.
/// </remarks>
internal sealed class RunRecoverer
{
    private readonly IFlowRunStore _runStore;
    private readonly IFlowRunRuntimeStore _runtimeStore;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly IStepDispatcher _dispatcher;
    private readonly IOutputsRepository _outputsRepository;
    private readonly ILogger _logger;

    /// <summary>Initialises the recoverer with the dependencies inherited from the host service.</summary>
    public RunRecoverer(
        IFlowRunStore runStore,
        IFlowRunRuntimeStore runtimeStore,
        IFlowGraphPlanner graphPlanner,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository,
        ILogger logger)
    {
        _runStore = runStore;
        _runtimeStore = runtimeStore;
        _graphPlanner = graphPlanner;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
        _logger = logger;
    }

    /// <summary>
    /// Re-dispatches orphaned ready / waiting steps for one run; if nothing is left to
    /// dispatch and no step is in-flight, closes the run with the canonical terminal
    /// status from <see cref="RunTerminationClassifier.ComputeTerminalStatus"/>.
    /// </summary>
    /// <param name="run">The active run record being recovered.</param>
    /// <param name="flow">The flow definition matching <paramref name="run"/>.</param>
    /// <param name="ct">Cancellation token from the host's <c>StartAsync</c>.</param>
    public async Task RecoverRunAsync(FlowRunRecord run, IFlowDefinition flow, CancellationToken ct)
    {
        var statuses = await _runtimeStore.GetStepStatusesAsync(run.Id).ConfigureAwait(false);
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

        var terminalStatus = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);
        try
        {
            await _runStore.CompleteRunAsync(run.Id, terminalStatus).ConfigureAwait(false);
            _logger.LogInformation(
                "FlowRunRecoveryHostedService: closed zombie run {RunId} as {Status} (all steps terminal but run remained Running).",
                run.Id, terminalStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "FlowRunRecoveryHostedService: failed to close zombie run {RunId}.", run.Id);
        }
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

        var jobId = delay.HasValue
            ? await _dispatcher.ScheduleStepAsync(ctx, flow, step, delay.Value, ct).ConfigureAwait(false)
            : await _dispatcher.EnqueueStepAsync(ctx, flow, step, ct).ConfigureAwait(false);

        if (jobId is not null)
        {
            try
            {
                await _runStore.AnnotateDispatchAsync(ctx.RunId, step.Key, jobId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "FlowRunRecoveryHostedService: could not restore trigger data for run {RunId}.", run.Id);
        }
        return ctx;
    }
}
