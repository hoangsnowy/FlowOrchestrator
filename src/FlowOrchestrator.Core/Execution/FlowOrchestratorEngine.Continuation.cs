using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// DAG-continuation partial of <see cref="FlowOrchestratorEngine"/>: full-graph
/// evaluation with When-skip propagation, and the
/// terminal-status decision via <see cref="RunTerminationClassifier"/>.
/// </summary>
public sealed partial class FlowOrchestratorEngine
{
    /// <summary>
    /// Drives the run forward after <paramref name="step"/> reached a status: cascade-skips blocked
    /// steps, advances parked loops, evaluates <c>When</c> clauses, dispatches every ready step, and —
    /// when nothing is left to do — completes the run.
    /// </summary>
    /// <param name="ctx">Execution context of the step whose completion triggered this continuation.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="step">The step that just reached a status; used for loop-event attribution.</param>
    /// <param name="result">The step's result; its <see cref="IStepResult.DelayNextStep"/> delays the dependents.</param>
    /// <remarks>
    /// <para>
    /// The body runs in passes until a fixpoint. One pass is: blocked-step cascade-skip, loop advance,
    /// then ready-step <c>When</c>-evaluation and dispatch. A <c>When</c>-skip writes a status row that
    /// can block a dependent (which then needs a cascade-skip row of its own, or the loop iteration it
    /// belongs to never settles) or unblock one, so a pass that skipped anything is followed by another
    /// pass on the re-read map. Before this the blocked pass ran once, ahead of the <c>When</c> loop, so
    /// the dependents of a <c>When</c>-skipped step never got a row.
    /// </para>
    /// <para>
    /// The completion gate decides on ONE status snapshot: nothing in flight, no claim or dispatch-ledger
    /// row without a status row, and — the part that closes the premature-completion race — no step the
    /// planner reports ready or blocked on that same snapshot. A parallel branch completing on another
    /// worker writes its terminal row and only then starts its own continuation. If the gate's snapshot
    /// already holds that row, a join it unlocked is ready here, and this continuation goes round again
    /// and dispatches it itself; the ledger absorbs the other worker's duplicate attempt. If the snapshot
    /// does not hold the row, the branch reads as in flight and this gate stays open, and the other
    /// worker's continuation — whose first read follows both terminal writes — sees the join ready.
    /// Either way the join is dispatched before anyone can close the run.
    /// </para>
    /// <para>
    /// <b>Complexity per step completion.</b> Each pass is O(n) in the status-map size for the planner's
    /// evaluation — the same order as the status read it evaluates, which already returns every row —
    /// plus O(1) store calls per ready or blocked step. Extra passes happen only when the previous one
    /// wrote a skip row, so they are bounded by the depth of the skip cascade one completion triggers;
    /// the common completion runs exactly one pass. On the non-enqueue path the gate costs three reads
    /// (statuses, claims, ledger) plus one evaluation of the snapshot it already holds. The previous gate
    /// cost five reads (statuses and claims, then statuses, claims and ledger again inside
    /// <c>TryCompleteRunAsync</c>), so the race fix saves two round-trips per completion that reaches it.
    /// </para>
    /// </remarks>
    private async Task RunGraphContinuationAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result)
    {
        var statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
        FlowGraphEvaluation? evaluation = null;

        string? termination = null;
        var terminationResolved = false;

        // Admitted iterations count as enqueued work: they hold a dispatch-ledger row but no status
        // row yet, so this continuation must not take a completion decision under them.
        var enqueued = 0;

        for (var pass = 1; ; pass++)
        {
            evaluation ??= _graphPlanner.Evaluate(flow, statuses);

            // Re-read only when this pass actually wrote a status row: most completions unblock no
            // dependents at all, and re-fetching costs a round-trip returning every step row in the run.
            if (await SkipBlockedStepsAsync(ctx, flow, evaluation.BlockedStepKeys).ConfigureAwait(false))
            {
                statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
            }

            // Loop advance: the step that just finished — or one skipped by this or an earlier pass — may
            // have been the last outstanding child of an enclosing loop, which either frees a concurrency
            // slot for the next iteration or settles the loop. Runs before the ready-step evaluation so
            // the steps it unblocks become ready in this same pass, and after the blocked pass because a
            // child skipped there is exactly what can complete the last iteration.
            var (admitted, settledAny) = await AdvanceLoopsAsync(ctx, flow, step, statuses).ConfigureAwait(false);
            enqueued += admitted;
            if (admitted > 0 || settledAny)
            {
                statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
            }

            evaluation = _graphPlanner.Evaluate(flow, statuses);

            if (!terminationResolved)
            {
                termination = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);
                terminationResolved = true;
            }

            var anyWhenSkipped = false;
            if (termination is null)
            {
                foreach (var readyStepKey in evaluation.ReadyStepKeys)
                {
                    var metadata = flow.Manifest.Steps.FindStep(readyStepKey);
                    if (metadata is null)
                    {
                        continue;
                    }

                    if (await TryEvaluateWhenAndSkipAsync(ctx, flow, readyStepKey).ConfigureAwait(false))
                    {
                        anyWhenSkipped = true;
                        continue;
                    }

                    var nextStep = new StepInstance(readyStepKey, metadata.Type)
                    {
                        RunId = ctx.RunId,
                        PrincipalId = ctx.PrincipalId,
                        TriggerData = ctx.TriggerData,
                        TriggerHeaders = ctx.TriggerHeaders,
                        ScheduledTime = DateTimeOffset.UtcNow,
                        Inputs = new Dictionary<string, object?>(metadata.Inputs)
                    };

                    if (await TryScheduleStepAsync(ctx, flow, nextStep, result.DelayNextStep).ConfigureAwait(false))
                    {
                        enqueued++;
                    }
                }
            }

            if (anyWhenSkipped && !PassLimitReached(flow, pass))
            {
                // The skip rows just written can block dependents (cascade-skipped next pass), unblock
                // others, or complete a loop iteration: go round on a fresh map.
                statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
                evaluation = null;
                continue;
            }

            if (enqueued > 0)
            {
                return;
            }

            // Completion gate — every check below reads the same snapshot.
            statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
            if (await HasInFlightWorkAsync(ctx.RunId, statuses).ConfigureAwait(false))
            {
                return;
            }

            // A step the planner reports ready on this snapshot (dependencies terminal, no status row, and
            // no ledger row — the in-flight check just returned for a dispatched one) or blocked (owed its
            // cascade-skip row) is work nobody has done yet. Do it here rather than close the run: the
            // continuation that unlocked it may have read an older map and walked away. Under a
            // cancel/timeout verdict ready steps are never dispatched, so they do not hold the run open.
            evaluation = _graphPlanner.Evaluate(flow, statuses);
            if (evaluation.BlockedStepKeys.Count > 0 || (termination is null && evaluation.ReadyStepKeys.Count > 0))
            {
                if (PassLimitReached(flow, pass))
                {
                    // Safety net against a cascade that does not converge. Leave the run open rather than
                    // close it under unfinished work; recovery re-dispatches ready steps.
                    EngineLog.ContinuationPassLimitReached(_logger, ctx.RunId, pass);
                    return;
                }

                continue;
            }

            // Single source of truth — same classifier that FlowRunRecoveryHostedService uses.
            termination ??= RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

            await CompleteActiveRunAsync(ctx.RunId, termination).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> once <paramref name="pass"/> exceeds the number of passes a
    /// converging continuation could need: one per step in the manifest, across every scope, plus slack.
    /// </summary>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="pass">The 1-based number of the pass that just finished.</param>
    /// <remarks>
    /// Every extra pass is caused by a status row the previous one wrote, so a converging cascade needs
    /// at most one pass per dependency level. The deep count walks nested scopes and is only taken once
    /// the cheap top-level bound is exceeded, so the common completion never pays for it.
    /// </remarks>
    private static bool PassLimitReached(IFlowDefinition flow, int pass)
    {
        const int slack = 4;
        return pass > flow.Manifest.Steps.Count + slack && pass > CountStepsDeep(flow.Manifest.Steps) + slack;

        static int CountStepsDeep(StepCollection steps)
        {
            var count = 0;
            foreach (var metadata in steps.Values)
            {
                count++;
                if (metadata is IScopedStep scoped)
                {
                    count += CountStepsDeep(scoped.Steps);
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Records a <see cref="StepStatus.Skipped"/> row with <see cref="StepSkipReasons.PrerequisitesUnmet"/>
    /// for every blocked step whose claim this worker wins.
    /// </summary>
    /// <param name="ctx">Execution context of the run.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="blockedStepKeys">Steps whose dependencies reached a terminal status they do not accept.</param>
    /// <returns><see langword="true"/> when at least one skip row was written.</returns>
    /// <remarks>O(1) store calls per blocked step: a claim, plus a skip write when the claim is won.</remarks>
    private async Task<bool> SkipBlockedStepsAsync(IExecutionContext ctx, IFlowDefinition flow, IReadOnlyList<string> blockedStepKeys)
    {
        var skippedAny = false;
        foreach (var blockedStepKey in blockedStepKeys)
        {
            if (!await _runtimeStore.TryClaimStepAsync(ctx.RunId, blockedStepKey).ConfigureAwait(false))
            {
                continue;
            }

            skippedAny = true;

            var metadata = flow.Manifest.Steps.FindStep(blockedStepKey);
            await _runtimeStore.RecordSkippedStepAsync(
                ctx.RunId,
                blockedStepKey,
                metadata?.Type ?? "Unknown",
                StepSkipReasons.PrerequisitesUnmet).ConfigureAwait(false);

            if (_observabilityOptions.EnableOpenTelemetry)
            {
                _telemetry.StepSkippedCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                    new KeyValuePair<string, object?>("step_key", blockedStepKey),
                    new KeyValuePair<string, object?>("reason", "prerequisites_unmet"));
            }
        }

        return skippedAny;
    }

    /// <summary>
    /// Advances every loop step currently parked on its barrier: admits the iterations its
    /// <see cref="LoopStepMetadata.ConcurrencyLimit"/> now has room for, then settles the loops
    /// whose iterations have all reached a terminal status.
    /// </summary>
    /// <param name="ctx">Execution context of the child step whose completion triggered this pass.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="step">The step that just reached a terminal status; used only for event attribution.</param>
    /// <param name="statuses">Status map read after the blocked-step pass.</param>
    /// <returns>
    /// The number of loop-body entry steps this pass enqueued, and whether any loop settled.
    /// </returns>
    /// <remarks>
    /// Admission runs first so a freed concurrency slot is filled in the same pass that freed it.
    /// It cannot settle a loop early: an un-admitted iteration has no status rows, so the barrier
    /// reads it as outstanding either way.
    /// </remarks>
    private async Task<(int Admitted, bool Settled)> AdvanceLoopsAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance step,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var admitted = await AdmitLoopIterationsAsync(ctx, flow, statuses).ConfigureAwait(false);

        // Re-read only when admission changed something: the admitted steps hold dispatch rows the
        // settle pass never looks at, but an admitted-and-already-executed step would.
        var settleView = admitted > 0
            ? await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false)
            : statuses;

        var settled = await SettleEnclosingLoopsAsync(ctx, flow, step, settleView).ConfigureAwait(false);
        return (admitted, settled);
    }

    /// <summary>
    /// Dispatches the entry steps of every iteration that the concurrency gate can admit right now,
    /// across every loop step still parked on its barrier.
    /// </summary>
    /// <param name="ctx">Execution context of the run being advanced.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="statuses">Status map as read for this pass.</param>
    /// <returns>The number of entry steps this pass actually enqueued.</returns>
    /// <remarks>
    /// <para>
    /// Every candidate loop in the run is considered, not just the ones enclosing the completed
    /// step, for the same reason the settle pass does: a cascade-skip elsewhere in the graph can
    /// free a slot in a sibling loop that then has no completion of its own left to advance it.
    /// </para>
    /// <para>
    /// Dispatch goes through <see cref="TryScheduleStepAsync"/>, so the ledger absorbs a duplicate
    /// admission decided concurrently on another worker and only the winner is counted. The
    /// iteration count is read from the loop's own persisted output — the same source the barrier
    /// uses — so a loop whose output cannot be read admits nothing rather than guessing.
    /// </para>
    /// </remarks>
    private async Task<int> AdmitLoopIterationsAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var candidates = LoopBarrier.RunningLoopKeys(flow, statuses);
        if (candidates.Count == 0)
        {
            return 0;
        }

        // Ledger membership by point lookup, not by fetching the set: the ledger holds one row per
        // dispatched step, so reading it whole on every pass while a loop is parked was O(n) payload
        // per step completion (#189). NextAdmissionsAsync consults it only for the boundary probes
        // the status map cannot answer — one lookup per entry child in the sequential steady state.
        var runId = ctx.RunId;
        var runStore = _runStore;
        Func<string, Task<bool>> isDispatched = key => runStore.IsStepDispatchedAsync(runId, key);
        var admitted = 0;

        foreach (var loopKey in candidates)
        {
            var loopMetadata = flow.Manifest.Steps.FindStep(loopKey);
            if (loopMetadata is not IScopedStep)
            {
                continue;
            }

            var output = await _outputsRepository.GetStepOutputAsync(ctx.RunId, loopKey).ConfigureAwait(false);
            if (!LoopBarrier.TryReadIterationCount(output, out var iterations))
            {
                continue;
            }

            var requests = await LoopAdmission
                .NextAdmissionsAsync(loopMetadata, loopKey, iterations, statuses, isDispatched)
                .ConfigureAwait(false);
            foreach (var request in requests)
            {
                var childStep = new StepInstance(request.RuntimeStepKey, request.Metadata.Type)
                {
                    RunId = ctx.RunId,
                    PrincipalId = ctx.PrincipalId,
                    TriggerData = ctx.TriggerData,
                    TriggerHeaders = ctx.TriggerHeaders,
                    ScheduledTime = DateTimeOffset.UtcNow,
                    // Template inputs only: DefaultStepExecutor re-derives __loopItem / __loopIndex
                    // from the runtime key via LoopScopeInputs, which is also what every other
                    // late-dispatch path (signal resume, retry, recovery) relies on.
                    Inputs = new Dictionary<string, object?>(request.Metadata.Inputs)
                };

                if (await TryScheduleStepAsync(ctx, flow, childStep, delay: null).ConfigureAwait(false))
                {
                    admitted++;
                    EngineLog.LoopIterationAdmitted(_logger, loopKey, request.Index, request.RuntimeStepKey);
                }
            }
        }

        return admitted;
    }

    /// <summary>
    /// Settles every loop step currently parked on its barrier whose iterations have all reached
    /// a terminal status, moving it from <see cref="StepStatus.Running"/> to
    /// <see cref="StepStatus.Succeeded"/> and emitting its deferred completion events.
    /// </summary>
    /// <param name="ctx">Execution context of the child step whose completion triggered this pass.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="step">The step that just reached a terminal status; used only for event attribution.</param>
    /// <param name="statuses">Status map read after the blocked-step pass.</param>
    /// <returns><see langword="true"/> when at least one loop step was settled.</returns>
    /// <remarks>
    /// <para>
    /// A loop step is the only step whose completion is decided outside its own handler, so its
    /// <c>step.completed</c> event and <see cref="StepCompletedEvent"/> are published here rather
    /// than in <c>RunStepAsync</c> — at the point the loop is actually finished, not at fan-out.
    /// </para>
    /// <para>
    /// The candidate set is every <see cref="StepStatus.Running"/> scoped step in the run, not just
    /// the loops enclosing <paramref name="step"/>. The blocked-step and <c>When</c>-skip passes can
    /// cascade-skip a step anywhere in the graph, including the last outstanding iteration of a
    /// <i>sibling</i> loop; that loop then has no later completion left to settle it and would stay
    /// Running forever, keeping <c>HasInFlightWorkAsync</c> true and stranding the run. Scanning all
    /// parked loops is a strict superset of the enclosing set — <see cref="LoopBarrier.SettleAsync"/>
    /// already ignores any candidate that is not Running — and costs one output read per parked loop.
    /// </para>
    /// <para>
    /// The completed step itself is included when it is scoped and still Running, which covers
    /// re-running a loop step whose iterations already finished: <see cref="RetryStepAsync"/> clears
    /// the dispatch ledger for the retried key only, so the re-executed <c>ForEach</c> re-arms the
    /// barrier while its children are suppressed as already-dispatched and can never settle it again.
    /// On the ordinary fan-out path this candidate is a no-op — the children have no status rows yet.
    /// </para>
    /// </remarks>
    private async Task<bool> SettleEnclosingLoopsAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance step,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var candidates = LoopBarrier.RunningLoopKeys(flow, statuses);
        if (candidates.Count == 0)
        {
            return false;
        }

        var settled = await LoopBarrier
            .SettleAsync(flow, ctx.RunId, candidates, statuses, _outputsRepository, _runStore)
            .ConfigureAwait(false);

        // `settled` holds only the loops whose Running → Succeeded swap THIS call won, so a loop that
        // two continuations decide at once publishes one event, not two. O(settled loops), no reads.
        foreach (var loopKey in settled)
        {
            await PublishEventSafelyAsync(new StepCompletedEvent
            {
                RunId = ctx.RunId,
                StepKey = loopKey,
                Status = StepStatus.Succeeded.ToString()
            }, CancellationToken.None).ConfigureAwait(false);
        }

        return settled.Count > 0;
    }

    /// <summary>
    /// Evaluates the <c>When</c> clauses on <paramref name="stepKey"/>'s metadata. If any
    /// clause returns <see langword="false"/>, marks the step <see cref="StepStatus.Skipped"/>
    /// and persists the evaluation trace for dashboard display.
    /// </summary>
    /// <returns><see langword="true"/> when the step was skipped due to a false When clause.</returns>
    private async Task<bool> TryEvaluateWhenAndSkipAsync(IExecutionContext ctx, IFlowDefinition flow, string stepKey)
    {
        var metadata = flow.Manifest.Steps.FindStep(stepKey);
        if (metadata is null || metadata.RunAfter.Count == 0)
        {
            return false;
        }

        var hasAnyWhen = metadata.RunAfter.Values.Any(v => !string.IsNullOrWhiteSpace(v.When));
        if (!hasAnyWhen)
        {
            return false;
        }

        using var whenActivity = _observabilityOptions.EnableOpenTelemetry
            ? _telemetry.ActivitySource.StartActivity("flow.step.when", ActivityKind.Internal)
            : null;
        whenActivity?.SetTag("flow.id", flow.Id.ToString());
        whenActivity?.SetTag("run.id", ctx.RunId.ToString());
        whenActivity?.SetTag("step.key", stepKey);

        WhenEvaluationTrace? trace;
        try
        {
            trace = await _whenEvaluator.EvaluateAsync(ctx, flow, metadata).ConfigureAwait(false);
        }
        catch (FlowExpressionException ex)
        {
            EngineLog.WhenEvaluationFailed(_logger, ex, stepKey);
            // Surface to the parent flow.step activity (if any) so APMs see a failure event.
            Activity.Current?.RecordError(ex);
            // A malformed expression is an authoring error. Skip with a synthetic trace
            // so the dashboard surfaces the problem to the user.
            trace = new WhenEvaluationTrace
            {
                Expression = ex.Expression,
                Resolved = ex.Message,
                Result = false
            };
        }

        if (trace is null)
        {
            return false;
        }

        if (!await _runtimeStore.TryClaimStepAsync(ctx.RunId, stepKey).ConfigureAwait(false))
        {
            return false;
        }

        var traceJson = JsonSerializer.Serialize(trace, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var reason = $"When clause '{trace.Expression}' evaluated to false ({trace.Resolved}).";
        await _runtimeStore.RecordSkippedStepAsync(ctx.RunId, stepKey, metadata.Type, reason, traceJson).ConfigureAwait(false);

        whenActivity?.SetTag("flow.when.expression", trace.Expression);
        whenActivity?.SetTag("flow.when.resolved", trace.Resolved);
        whenActivity?.SetTag("flow.when.result", false);
        if (_observabilityOptions.EnableOpenTelemetry)
        {
            _telemetry.StepSkippedCounter.Add(
                1,
                new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                new KeyValuePair<string, object?>("step_key", stepKey),
                new KeyValuePair<string, object?>("reason", "when_false"));
        }

        return true;
    }
}
