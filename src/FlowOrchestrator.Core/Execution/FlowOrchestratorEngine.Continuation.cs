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
/// DAG-continuation partial of <see cref="FlowOrchestratorEngine"/>: legacy sequential
/// next-step resolution, full-graph evaluation with When-skip propagation, and the
/// terminal-status decision via <see cref="RunTerminationClassifier"/>.
/// </summary>
public sealed partial class FlowOrchestratorEngine
{
    private async Task RunLegacySequentialContinuationAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result)
    {
        var next = await _flowExecutor.GetNextStep(ctx, flow, step, result).ConfigureAwait(false);
        if (next is not null)
        {
            await TryScheduleStepAsync(ctx, flow, next, result.DelayNextStep).ConfigureAwait(false);
            return;
        }

        await _runStore.CompleteRunAsync(ctx.RunId, result.Status.ToString()).ConfigureAwait(false);
        await RecordEventAsync(
            ctx,
            flow,
            step,
            "run.completed",
            $"Run completed with status {result.Status}.").ConfigureAwait(false);
    }

    private async Task RunGraphContinuationAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result)
    {
        var statuses = await _runtimeStore!.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
        var evaluation = _graphPlanner.Evaluate(flow, statuses);

        foreach (var blockedStepKey in evaluation.BlockedStepKeys)
        {
            if (!await _runtimeStore.TryClaimStepAsync(ctx.RunId, blockedStepKey).ConfigureAwait(false))
            {
                continue;
            }

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

            await RecordEventAsync(
                ctx,
                flow,
                step,
                "step.skipped",
                $"Step '{blockedStepKey}' skipped because dependencies did not match.",
                blockedStepKey).ConfigureAwait(false);
        }

        statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);

        // Loop advance: the step that just finished may have been the last outstanding child of an
        // enclosing loop — which either frees a concurrency slot for the next iteration, or, when no
        // iteration is left, settles the loop. Both run BEFORE evaluating the graph so the steps
        // they unblock become ready in this same pass. Runs after the blocked-step pass above,
        // because a child skipped there is exactly what can complete the last iteration.
        var (admitted, settledAny) = await AdvanceLoopsAsync(ctx, flow, step, statuses).ConfigureAwait(false);
        if (admitted > 0 || settledAny)
        {
            statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
        }

        evaluation = _graphPlanner.Evaluate(flow, statuses);

        var termination = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);

        // Admitted iterations count as enqueued work: they hold a dispatch-ledger row but no status
        // row yet, so the completion gate at the bottom of this method cannot see them and would
        // close the run out from under them.
        var enqueued = admitted;
        if (termination is null)
        {
            // When-evaluation may skip steps and unblock new dependents; loop until the
            // ready set is stable (no further skips produced).
            var safetyCounter = 0;
            while (true)
            {
                var anyWhenSkipped = false;
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

                if (!anyWhenSkipped) break;
                if (++safetyCounter > flow.Manifest.Steps.Count + 4) break;

                statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);

                // A When-skip recorded just above may have been the LAST outstanding child of an
                // enclosing loop — and it happened after the advance pass at the top of this method,
                // with no later step completion to re-trigger it. Advance here so the next iteration
                // is admitted (or the loop's downstream steps become ready) in the next sweep
                // instead of parking the run.
                var (whenAdmitted, whenSettled) = await AdvanceLoopsAsync(ctx, flow, step, statuses).ConfigureAwait(false);
                enqueued += whenAdmitted;
                if (whenAdmitted > 0 || whenSettled)
                {
                    statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
                }

                evaluation = _graphPlanner.Evaluate(flow, statuses);
            }
        }

        if (enqueued > 0)
        {
            return;
        }

        statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false);
        if (statuses.Values.Any(IsInFlight))
        {
            return;
        }

        var claimed = await _runtimeStore.GetClaimedStepKeysAsync(ctx.RunId).ConfigureAwait(false);
        if (claimed.Except(statuses.Keys, StringComparer.Ordinal).Any())
        {
            return;
        }

        // Single source of truth — same classifier that FlowRunRecoveryHostedService uses.
        termination ??= RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        await TryCompleteRunAsync(ctx.RunId, termination).ConfigureAwait(false);
        await RecordEventAsync(
            ctx,
            flow,
            step,
            "run.completed",
            $"Run completed with status {termination}.").ConfigureAwait(false);
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
            ? await _runtimeStore!.GetStepStatusesAsync(ctx.RunId).ConfigureAwait(false)
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
        if (_runtimeStore is null)
        {
            return 0;
        }

        var candidates = LoopBarrier.RunningLoopKeys(flow, statuses);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var dispatched = await _runStore.GetDispatchedStepKeysAsync(ctx.RunId).ConfigureAwait(false);
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

            var requests = LoopAdmission.NextAdmissions(loopMetadata, loopKey, iterations, statuses, dispatched);
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

        foreach (var loopKey in settled)
        {
            await PublishEventSafelyAsync(new StepCompletedEvent
            {
                RunId = ctx.RunId,
                StepKey = loopKey,
                Status = StepStatus.Succeeded.ToString()
            }, CancellationToken.None).ConfigureAwait(false);

            await RecordEventAsync(
                ctx,
                flow,
                step,
                "step.completed",
                $"Loop step '{loopKey}' completed: all iterations reached a terminal status.",
                loopKey).ConfigureAwait(false);
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

        if (_runtimeStore is null)
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

        await RecordEventAsync(
            ctx,
            flow,
            new StepInstance(stepKey, metadata.Type) { RunId = ctx.RunId, ScheduledTime = DateTimeOffset.UtcNow },
            "step.skipped",
            $"Step '{stepKey}' skipped because '{trace.Expression}' evaluated to false.",
            stepKey).ConfigureAwait(false);

        return true;
    }
}
