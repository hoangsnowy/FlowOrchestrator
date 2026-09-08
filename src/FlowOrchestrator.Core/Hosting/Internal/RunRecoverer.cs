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
/// orchestration loop while the per-run state machine (settle loop barriers, re-dispatch ready
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
    /// Settles finished loop barriers and re-dispatches orphaned ready steps for one run; if nothing is left to
    /// dispatch and no step is in-flight, closes the run with the canonical terminal
    /// status from <see cref="RunTerminationClassifier.ComputeTerminalStatus"/>.
    /// </summary>
    /// <param name="run">The active run record being recovered.</param>
    /// <param name="flow">The flow definition matching <paramref name="run"/>.</param>
    /// <param name="ct">Cancellation token from the host's <c>StartAsync</c>.</param>
    public async Task RecoverRunAsync(FlowRunRecord run, IFlowDefinition flow, CancellationToken ct)
    {
        var statuses = await _runtimeStore.GetStepStatusesAsync(run.Id).ConfigureAwait(false);

        // Loop barriers first: the host can have died between the last iteration completing and
        // the continuation settling its loop step, which would leave the loop Running forever and
        // its downstream steps unreachable. Settling here makes them Ready for the pass below.
        var settledLoops = await LoopBarrier.SettleAsync(
            flow,
            run.Id,
            LoopBarrier.RunningLoopKeys(flow, statuses),
            statuses,
            _outputsRepository,
            _runStore).ConfigureAwait(false);

        if (settledLoops.Count > 0)
        {
            _logger.LogInformation(
                "FlowRunRecoveryHostedService: settled {Count} completed loop step(s) for run {RunId}: {Keys}.",
                settledLoops.Count, run.Id, string.Join(", ", settledLoops));
            statuses = await _runtimeStore.GetStepStatusesAsync(run.Id).ConfigureAwait(false);
        }

        var dispatched = await _runStore.GetDispatchedStepKeysAsync(run.Id).ConfigureAwait(false);
        var evaluation = _graphPlanner.Evaluate(flow, statuses);

        var ctx = await BuildContextAsync(run).ConfigureAwait(false);
        var recovered = 0;

        // Throttled loops: the host can have died between an iteration settling and the
        // continuation admitting the next one. Nothing else recovers that — the loop step is
        // Running (so the zombie check below leaves the run alone) and the un-admitted iteration
        // has no status row (so it is never classified Ready). Re-running the admission gate here
        // is idempotent: it re-derives the same window from the ledger + status map.
        recovered += await AdmitLoopIterationsAsync(ctx, flow, run, statuses, dispatched, ct).ConfigureAwait(false);

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

        // NOTE: WaitingStepKeys are deliberately NOT dispatched here. "Waiting" means at least one
        // RunAfter dependency is still non-terminal (a Running step, a parked poll, a loop whose
        // barrier has not settled) — running such a step would execute it out of order, which is
        // the very failure mode issue #169 reported. A step whose dependencies ARE satisfied is
        // classified Ready and is recovered by the pass above; a polling step keeps its own status
        // row, so it is never classified Waiting in the first place.

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
    /// Re-runs the <see cref="LoopAdmission"/> gate for every loop step still parked on its
    /// barrier, dispatching the entry steps of any iteration the gate has room for.
    /// </summary>
    /// <param name="ctx">Execution context rebuilt for the run.</param>
    /// <param name="flow">The flow definition matching the run.</param>
    /// <param name="run">The active run record being recovered.</param>
    /// <param name="statuses">Status map read after the barrier settle pass.</param>
    /// <param name="dispatched">Step keys with a live dispatch-ledger row for the run.</param>
    /// <param name="ct">Cancellation token from the host's <c>StartAsync</c>.</param>
    /// <returns>The number of loop-body entry steps this pass enqueued.</returns>
    private async Task<int> AdmitLoopIterationsAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        FlowRunRecord run,
        IReadOnlyDictionary<string, StepStatus> statuses,
        IReadOnlySet<string> dispatched,
        CancellationToken ct)
    {
        var admitted = 0;

        foreach (var loopKey in LoopBarrier.RunningLoopKeys(flow, statuses))
        {
            var loopMetadata = flow.Manifest.Steps.FindStep(loopKey);
            if (loopMetadata is not IScopedStep)
            {
                continue;
            }

            var output = await _outputsRepository.GetStepOutputAsync(run.Id, loopKey).ConfigureAwait(false);
            if (!LoopBarrier.TryReadIterationCount(output, out var iterations))
            {
                continue;
            }

            var requests = LoopAdmission.NextAdmissions(loopMetadata, loopKey, iterations, statuses, dispatched);
            foreach (var request in requests)
            {
                var step = new StepInstance(request.RuntimeStepKey, request.Metadata.Type)
                {
                    RunId = run.Id,
                    ScheduledTime = DateTimeOffset.UtcNow,
                    Inputs = new Dictionary<string, object?>(request.Metadata.Inputs)
                };

                if (!await TryDispatchAsync(ctx, flow, step, delay: null, ct).ConfigureAwait(false))
                {
                    continue;
                }

                admitted++;
                _logger.LogInformation(
                    "FlowRunRecoveryHostedService: admitted loop '{LoopKey}' iteration {Index} for run {RunId} — enqueued '{StepKey}'.",
                    loopKey, request.Index, run.Id, request.RuntimeStepKey);
            }
        }

        return admitted;
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
