using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Observability;
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
    private readonly WhenSkipGate _whenGate;
    private readonly ILogger _logger;
    private readonly TimeSpan? _orphanedClaimTimeout;

    /// <summary>Step type recorded for a redelivered step whose key the manifest no longer contains.</summary>
    private const string UnknownStepType = "Unknown";

    /// <summary>Initialises the recoverer with the dependencies inherited from the host service.</summary>
    /// <param name="runStore">Run store (dispatch ledger, zombie close).</param>
    /// <param name="runtimeStore">Runtime store (statuses, orphaned claims).</param>
    /// <param name="graphPlanner">Planner that classifies ready steps.</param>
    /// <param name="dispatcher">Runtime adapter recovered steps are handed to.</param>
    /// <param name="outputsRepository">Outputs repository (trigger data, loop iteration counts).</param>
    /// <param name="whenGate">Gate that applies a ready step's <c>When</c> clauses before it is dispatched.</param>
    /// <param name="logger">Logger of the host service.</param>
    /// <param name="orphanedClaimTimeout">
    /// Age past which a claim with no step status row is released before the run is evaluated;
    /// <see langword="null"/> or non-positive turns the release off.
    /// </param>
    public RunRecoverer(
        IFlowRunStore runStore,
        IFlowRunRuntimeStore runtimeStore,
        IFlowGraphPlanner graphPlanner,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository,
        WhenSkipGate whenGate,
        ILogger logger,
        TimeSpan? orphanedClaimTimeout = null)
    {
        _runStore = runStore;
        _runtimeStore = runtimeStore;
        _graphPlanner = graphPlanner;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
        _whenGate = whenGate;
        _logger = logger;
        _orphanedClaimTimeout = orphanedClaimTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : null;
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
        // Orphaned claims first (RFC 2.0 S1b): a worker that died between winning a claim and writing the
        // step's first status row left a claim that makes every later attempt exit and keeps the run in
        // flight for ever. Releasing it (with its ledger row) makes the step plain "ready, never
        // dispatched", which the pass below re-dispatches. A blocked or When-skipped step orphaned the same
        // way stays without a row; the zombie check below then closes the run on the statuses it has.
        if (_orphanedClaimTimeout is { } orphanAge)
        {
            var released = await _runtimeStore.ReleaseOrphanedClaimsAsync(run.Id, orphanAge).ConfigureAwait(false);
            if (released.Count > 0)
            {
                HostingLog.RecoveryReleasedOrphanedClaims(_logger, released.Count, run.Id, string.Join(", ", released));
            }
        }

        var statuses = await _runtimeStore.GetStepStatusesAsync(run.Id).ConfigureAwait(false);
        var ledger = await _runStore.GetDispatchedStepKeysAsync(run.Id).ConfigureAwait(false);

        // The ledger snapshot plus every key this pass dispatches, so a later sweep of the loop below neither
        // re-records them nor mistakes them for never-dispatched work.
        var dispatched = new HashSet<string>(ledger, StringComparer.Ordinal);

        var ctx = await BuildContextAsync(run).ConfigureAwait(false);
        var recovered = 0;
        var failedDispatches = 0;
        FlowGraphEvaluation evaluation;

        // A non-durable runtime (the in-process channel) lost every queued and delayed step with the process
        // that held it, while the ledger rows survived in the store. Without this pass the sweep below reads
        // those rows as "already in the runtime queue" and skips the steps for good.
        if (ledger.Count > 0 && _dispatcher.LosesQueuedStepsOnRestart)
        {
            var redelivery = await RedeliverLostQueuedStepsAsync(ctx, flow, run, statuses, ledger).ConfigureAwait(false);
            recovered += redelivery.Dispatched;
            failedDispatches += redelivery.Failed;
        }

        // The same fixed point the engine's continuation reaches: a When-skip or blocked-skip recorded in one
        // sweep can unblock (or block) dependents, settle a loop or free a loop slot, so sweep again until a
        // sweep writes no status. Bounded like the continuation's loop. Each sweep is O(steps in the run);
        // recovery runs once per active run at startup, never per step completion.
        var sweeps = 0;
        while (true)
        {
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
                HostingLog.RecoverySettledLoops(_logger, settledLoops.Count, run.Id, string.Join(", ", settledLoops));
                statuses = await _runtimeStore.GetStepStatusesAsync(run.Id).ConfigureAwait(false);
            }

            evaluation = _graphPlanner.Evaluate(flow, statuses);

            // Throttled loops: the host can have died between an iteration settling and the
            // continuation admitting the next one. Nothing else recovers that — the loop step is
            // Running (so the zombie check below leaves the run alone) and the un-admitted iteration
            // has no status row (so it is never classified Ready). Re-running the admission gate here
            // is idempotent: it re-derives the same window from the ledger + status map. An admitted
            // entry step has no in-scope RunAfter, so — exactly as in the engine — no When clause applies.
            var admission = await AdmitLoopIterationsAsync(ctx, flow, run, statuses, dispatched, ct).ConfigureAwait(false);
            recovered += admission.Dispatched;
            failedDispatches += admission.Failed;

            var wroteStatus = false;

            // Blocked steps — a prerequisite finished in a status the clause does not accept and the host
            // died before the continuation recorded the dependent skipped (or a When-skip in the previous
            // sweep blocked it). Recorded exactly as the continuation's blocked-step pass does.
            foreach (var blockedKey in evaluation.BlockedStepKeys)
            {
                var blockedMetadata = flow.Manifest.Steps.FindStep(blockedKey);
                if (await _whenGate.TrySkipBlockedAsync(run.Id, blockedKey, blockedMetadata?.Type ?? "Unknown").ConfigureAwait(false))
                {
                    wroteStatus = true;
                    HostingLog.RecoverySkippedBlockedStep(_logger, blockedKey, run.Id);
                }
            }

            // Ready steps — re-enqueue if no dispatch record (crash between persist and enqueue).
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

                // When is evaluated only at schedule time; RunStepAsync never re-checks it. Recovery is a
                // scheduling path, so it applies the clause here — otherwise a host that died between a
                // parent's completion and its continuation runs a child whose When is false on restart.
                if (await _whenGate.IsFalseAndSkippedAsync(ctx, flow, stepKey, metadata).ConfigureAwait(false))
                {
                    wroteStatus = true;
                    HostingLog.RecoverySkippedWhenFalse(_logger, stepKey, run.Id);
                    continue;
                }

                var step = new StepInstance(stepKey, metadata.Type)
                {
                    RunId = run.Id,
                    ScheduledTime = DateTimeOffset.UtcNow,
                    Inputs = new Dictionary<string, object?>(metadata.Inputs)
                };

                switch (await TryDispatchIsolatedAsync(ctx, flow, step, ct).ConfigureAwait(false))
                {
                    case DispatchOutcome.Dispatched:
                        dispatched.Add(stepKey);
                        recovered++;
                        HostingLog.RecoveryRedispatchedReadyStep(_logger, stepKey, run.Id);
                        break;
                    case DispatchOutcome.Failed:
                        failedDispatches++;
                        break;
                }
            }

            if (!wroteStatus || ++sweeps > flow.Manifest.Steps.Count + 4)
            {
                break;
            }

            statuses = await _runtimeStore.GetStepStatusesAsync(run.Id).ConfigureAwait(false);
        }

        // NOTE: WaitingStepKeys are deliberately NOT dispatched here. "Waiting" means at least one
        // RunAfter dependency is still non-terminal (a Running step, a parked poll, a loop whose
        // barrier has not settled) — running such a step would execute it out of order, which is
        // the very failure mode issue #169 reported. A step whose dependencies ARE satisfied is
        // classified Ready and is recovered by the pass above; a polling step keeps its own status
        // row, so it is never classified Waiting in the first place.

        if (recovered > 0 || failedDispatches > 0)
        {
            // A failed dispatch released its ledger row, so its step is still Ready: the run is not a zombie,
            // and the next recovery pass dispatches it again.
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
            HostingLog.RecoveryNothingOrphaned(_logger, run.Id);
            return;
        }

        var terminalStatus = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);
        try
        {
            await _runStore.CompleteRunAsync(run.Id, terminalStatus).ConfigureAwait(false);
            HostingLog.RecoveryClosedZombieRun(_logger, run.Id, terminalStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HostingLog.RecoveryZombieCloseFailed(_logger, ex, run.Id);
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
    /// <param name="dispatched">Keys with a dispatch-ledger row for the run; extended with every key this pass dispatches.</param>
    /// <param name="ct">Cancellation token from the host's <c>StartAsync</c>.</param>
    /// <returns>The number of loop-body entry steps this pass enqueued, and the number whose dispatch failed.</returns>
    private async Task<(int Dispatched, int Failed)> AdmitLoopIterationsAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        FlowRunRecord run,
        IReadOnlyDictionary<string, StepStatus> statuses,
        HashSet<string> dispatched,
        CancellationToken ct)
    {
        var admitted = 0;
        var failed = 0;

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

                var outcome = await TryDispatchIsolatedAsync(ctx, flow, step, ct).ConfigureAwait(false);
                if (outcome == DispatchOutcome.Failed)
                {
                    failed++;
                    continue;
                }

                if (outcome != DispatchOutcome.Dispatched)
                {
                    continue;
                }

                dispatched.Add(request.RuntimeStepKey);
                admitted++;
                HostingLog.RecoveryAdmittedLoopIteration(_logger, loopKey, request.Index, run.Id, request.RuntimeStepKey);
            }
        }

        return (admitted, failed);
    }

    /// <summary>
    /// Re-enqueues every ledgered step that has not started — no status row, or a <see cref="StepStatus.Pending"/>
    /// poll whose delayed re-attempt was lost — and that no worker currently claims. Only called for a runtime
    /// whose queued steps do not survive a restart (<see cref="IStepDispatcher.LosesQueuedStepsOnRestart"/>).
    /// </summary>
    /// <param name="ctx">Execution context rebuilt for the run.</param>
    /// <param name="flow">The flow definition matching the run.</param>
    /// <param name="run">The active run record being recovered.</param>
    /// <param name="statuses">Status map read at the start of recovery.</param>
    /// <param name="ledger">Keys holding a dispatch-ledger row.</param>
    /// <returns>The number of steps re-enqueued and the number whose re-enqueue threw.</returns>
    /// <remarks>
    /// The ledger row already exists, so no second row is recorded: the step goes straight to the runtime.
    /// "Dispatch many, execute once" still holds — if the step is delivered twice (another replica sharing
    /// the store still holds it in its own channel), the execution claim lets only one attempt run. A claimed
    /// step is left alone: its claimer is either still running it or orphaned, and orphaned claims are released
    /// by the pass above once they are old enough. <c>When</c> is not re-evaluated: a ledgered step passed its
    /// <c>When</c> gate when it was scheduled. A ledgered key missing from the manifest is redelivered as well, so
    /// the engine records it removed instead of the run waiting on it for ever. Cost: one claim point-lookup per
    /// ledgered, not-yet-terminal key — once per active run at startup.
    /// </remarks>
    private async Task<(int Dispatched, int Failed)> RedeliverLostQueuedStepsAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        FlowRunRecord run,
        IReadOnlyDictionary<string, StepStatus> statuses,
        IReadOnlySet<string> ledger)
    {
        var redelivered = 0;
        var failed = 0;

        foreach (var stepKey in ledger)
        {
            if (statuses.TryGetValue(stepKey, out var status) && status != StepStatus.Pending)
            {
                continue;  // terminal, or Running under a claim
            }

            if (await _runtimeStore.IsStepClaimedAsync(run.Id, stepKey).ConfigureAwait(false))
            {
                continue;
            }

            // A ledgered key this host's manifest does not contain (the deploy that restarted the host removed
            // or renamed it) is redelivered too, not dropped: its ledger row with no status row would otherwise
            // read as in-flight work for ever and the run could never complete. The engine decides it — it is
            // recorded Skipped as removed, or handed back if another manifest version is still live.
            var metadata = flow.Manifest.Steps.FindStep(stepKey);
            var step = new StepInstance(stepKey, metadata?.Type ?? UnknownStepType)
            {
                RunId = run.Id,
                ScheduledTime = DateTimeOffset.UtcNow,
                Inputs = metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(metadata.Inputs)
            };

            try
            {
                // CancellationToken.None: the ledger row is already durable (RFC 2.0 P-1(d)).
                await _dispatcher.EnqueueStepAsync(ctx, flow, step, CancellationToken.None).ConfigureAwait(false);
                redelivered++;
                HostingLog.RecoveryRedeliveredLostStep(_logger, stepKey, run.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The ledger row stays, so the next startup tries again.
                failed++;
                HostingLog.RecoveryRedeliveryFailed(_logger, ex, stepKey, run.Id);
            }
        }

        return (redelivered, failed);
    }

    /// <summary>
    /// <see cref="TryDispatchAsync"/> with per-step fault isolation: a dispatch that throws is logged and
    /// reported as <see cref="DispatchOutcome.Failed"/>, so the remaining ready steps of the run are still
    /// recovered instead of the whole run being abandoned until the next restart.
    /// </summary>
    /// <param name="ctx">Execution context rebuilt for the run.</param>
    /// <param name="flow">The flow definition matching the run.</param>
    /// <param name="step">The step to dispatch.</param>
    /// <param name="ct">Host token; honoured only before the ledger row is written.</param>
    /// <returns>What happened to the dispatch.</returns>
    private async Task<DispatchOutcome> TryDispatchIsolatedAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance step,
        CancellationToken ct)
    {
        try
        {
            return await TryDispatchAsync(ctx, flow, step, delay: null, ct).ConfigureAwait(false)
                ? DispatchOutcome.Dispatched
                : DispatchOutcome.LostRace;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // TryDispatchAsync already released the ledger row, so the step stays Ready for the next pass.
            HostingLog.RecoveryDispatchFailed(_logger, ex, step.Key, ctx.RunId);
            return DispatchOutcome.Failed;
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

        // The ledger row is durable from here (RFC 2.0 P-1(d), review C10): the host's StartAsync token must
        // not reach the dispatch. A shutdown that cancelled it here would abort the hand-off after the row
        // was recorded. Cancellation is honoured above, before the row is written, and by the host loop
        // between runs.
        string? jobId;
        try
        {
            jobId = delay.HasValue
                ? await _dispatcher.ScheduleStepAsync(ctx, flow, step, delay.Value, CancellationToken.None).ConfigureAwait(false)
                : await _dispatcher.EnqueueStepAsync(ctx, flow, step, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Catch-all-and-rethrow: the ledger row recorded above claims "an attempt is queued" for an
            // attempt the runtime never received, and recovery skips ledgered keys — left in place it
            // strands the step for good. Release it (CancellationToken.None: also on shutdown
            // cancellation) so the next recovery pass can dispatch again; a duplicate delivery, if the
            // runtime did accept the message, is absorbed by the step claim. Mirrors the engine's
            // DispatchReservedStepAsync.
            try
            {
                await _runStore.ReleaseDispatchAsync(ctx.RunId, step.Key, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception releaseFailure) when (releaseFailure is not OperationCanceledException)
            {
                HostingLog.RecoveryDispatchReleaseFailed(_logger, releaseFailure, step.Key);
            }

            throw;
        }

        if (jobId is not null)
        {
            try
            {
                await _runStore.AnnotateDispatchAsync(ctx.RunId, step.Key, jobId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                HostingLog.RecoveryDispatchAnnotateFailed(_logger, ex, step.Key);
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
            HostingLog.RecoveryTriggerDataUnavailable(_logger, ex, run.Id);
        }
        return ctx;
    }

    /// <summary>Result of one recovery dispatch attempt.</summary>
    private enum DispatchOutcome
    {
        /// <summary>This pass recorded the ledger row and handed the step to the runtime.</summary>
        Dispatched,

        /// <summary>Another replica (or an earlier pass) already holds the ledger row.</summary>
        LostRace,

        /// <summary>The runtime threw; the ledger row was released and the step stays Ready.</summary>
        Failed,
    }
}
