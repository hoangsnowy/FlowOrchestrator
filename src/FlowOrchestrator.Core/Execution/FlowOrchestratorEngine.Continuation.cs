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
/// DAG-continuation partial of <see cref="FlowOrchestratorEngine"/>: frontier evaluation with When-skip and
/// blocked-step propagation, loop admission and settling, and the run-completion gate whose terminal status
/// comes from <see cref="RunTerminationClassifier"/>.
/// </summary>
public sealed partial class FlowOrchestratorEngine
{
    /// <summary>Key of the synthetic step the trigger path continues from when every entry step was When-skipped.</summary>
    internal const string RunLevelStepKey = "__run";

    private static readonly FlowGraphEvaluation EmptyEvaluation = new();

    /// <summary>
    /// Drives the run forward after <paramref name="step"/> reached a status: cascade-skips blocked
    /// steps, advances parked loops, evaluates <c>When</c> clauses, dispatches every ready step, and —
    /// when nothing is left to do — completes the run.
    /// </summary>
    /// <param name="ctx">Execution context of the step whose completion triggered this continuation.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="step">The step that just reached a status.</param>
    /// <param name="result">The step's result; its <see cref="IStepResult.DelayNextStep"/> delays the dependents.</param>
    /// <param name="changedStepKeys">
    /// The steps whose rows were written, when that is not <paramref name="step"/> itself — the trigger path
    /// passes the entry steps it When-skipped. <see langword="null"/> means <paramref name="step"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Frontier, not the run (#189 P0-1, RFC 2.0 §3.5).</b> A step's classification is a function of its own
    /// row and its dependencies' rows. When step <c>X</c> changes, only <c>D(X)</c> — the steps that list
    /// <c>X</c> in <c>RunAfter</c>, in <c>X</c>'s scope instance — can change classification. Each pass
    /// therefore reads the rows of <c>D(changed) ∪ deps(D(changed))</c> by key
    /// (<see cref="IFlowRunRuntimeStore.GetStepStatusesAsync(Guid, IReadOnlyCollection{string})"/>) and classifies
    /// only <c>D(changed)</c>. A pass that writes rows — a blocked or <c>When</c> skip, a loop settle — feeds
    /// those keys to the next pass, so a cascade is walked wave by wave instead of by re-reading the run.
    /// </para>
    /// <para>
    /// <b>Write skew.</b> Take a join <c>J</c> waiting on <c>A</c> and <c>C</c>, which complete on two workers.
    /// Each worker's frontier read is a separate statement issued after its own status write committed (the
    /// write is awaited in <c>RunStepAsync</c> before this method runs, and every skip or settle below is
    /// awaited before the next pass reads). Let A commit first: C's read starts after C's commit, which is
    /// after A's, so it sees both rows and finds <c>J</c> ready. In every interleaving the last committer
    /// observes all prior commits, and the dispatch ledger is insert-if-absent, so a double "ready" costs
    /// nothing and a double "waiting" cannot happen.
    /// </para>
    /// <para>
    /// <b>Completion gate.</b> Only a continuation that dispatched nothing reaches it. Its first check is
    /// <see cref="IFlowRunRuntimeStore.HasRunningOrPendingStepAsync"/>: an open row is work that runs a
    /// continuation of its own later — and a ForEach is <see cref="StepStatus.Running"/> on its barrier for its
    /// whole life — so the gate stands down without reading the run, exactly where the full-map gate used to
    /// find the same row and stand down. Only with no open row does it read the whole status map, claims and
    /// ledger, and run a full <see cref="IFlowGraphPlanner.Evaluate"/> on that one snapshot: nothing in flight,
    /// no claimed or dispatched key without a row, and no step ready or blocked. That last check is what keeps
    /// the run open when another worker wrote a terminal row but has not dispatched its successor yet — the
    /// gate then dispatches the successor itself (the ledger absorbs the duplicate) instead of closing.
    /// </para>
    /// <para>
    /// <b>Complexity per step completion.</b> O(Σ fan-out · fan-in) keys read per pass, plus O(1) store calls
    /// per ready, blocked or parked-loop step (see <see cref="AdvanceLoopAsync"/> for the loop's own cost), plus
    /// one indexed existence query at the gate. Nothing is proportional to the run's status rows except the
    /// closing read, which happens once a run has no open step left — at its end. Extra passes happen only when
    /// a pass wrote a row, bounded by the depth of the skip/settle cascade one completion triggers.
    /// </para>
    /// </remarks>
    private async Task RunGraphContinuationAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance step,
        IStepResult result,
        IReadOnlyList<string>? changedStepKeys = null)
    {
        var runId = ctx.RunId;

        // A synthetic run-level step with no list of what changed names no row to start from: fall back to one
        // full evaluation (at the trigger that covers only the entry rows).
        var isRunLevel = string.Equals(step.Key, RunLevelStepKey, StringComparison.Ordinal);
        var evaluateFully = isRunLevel && changedStepKeys is null;
        List<string> changed = changedStepKeys is not null ? [.. changedStepKeys] : evaluateFully ? [] : [step.Key];

        // The completed step itself may be a loop parked on its barrier: just fanned out, or re-run by a retry
        // after its iterations had all finished (RetryStepAsync clears the ledger of the retried key only, so
        // the children are suppressed as already dispatched and could never settle it again).
        var selfLoopKey = isRunLevel ? null : step.Key;
        FlowGraphEvaluation? carried = null;
        List<string>? next = null;

        string? termination = null;
        var terminationResolved = false;

        // Admitted iterations count as enqueued work: they hold a dispatch-ledger row but no status
        // row yet, so this continuation must not take a completion decision under them.
        var enqueued = 0;

        for (var pass = 1; ; pass++)
        {
            next = null;

            // Loop advance first: a change in an iteration may have settled it, which frees a concurrency slot
            // or settles the loop. A settled loop is a change of its own, evaluated next pass.
            if (changed.Count > 0 || selfLoopKey is not null)
            {
                var advance = await AdvanceLoopsAsync(ctx, flow, changed, selfLoopKey).ConfigureAwait(false);
                selfLoopKey = null;
                enqueued += advance.Admitted;
                if (advance.Settled is { } settled)
                {
                    (next ??= []).AddRange(settled);
                }
            }

            FlowGraphEvaluation evaluation;
            if (carried is not null)
            {
                evaluation = carried;
                carried = null;
            }
            else
            {
                evaluation = await EvaluateChangesAsync(flow, runId, changed, evaluateFully).ConfigureAwait(false);
                evaluateFully = false;
            }

            if (evaluation.BlockedStepKeys.Count > 0)
            {
                await SkipBlockedStepsAsync(ctx, flow, evaluation.BlockedStepKeys, next ??= []).ConfigureAwait(false);
            }

            if (!terminationResolved)
            {
                termination = await ResolveTerminationStatusAsync(runId).ConfigureAwait(false);
                terminationResolved = true;
            }

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
                        (next ??= []).Add(readyStepKey);
                        continue;
                    }

                    var nextStep = new StepInstance(readyStepKey, metadata.Type)
                    {
                        RunId = runId,
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

            if (next is { Count: > 0 })
            {
                if (PassLimitReached(flow, pass))
                {
                    // Safety net against a cascade that does not converge. Leave the run open rather than
                    // close it under unfinished work; recovery re-evaluates the whole graph.
                    EngineLog.ContinuationPassLimitReached(_logger, runId, pass);
                    return;
                }

                // The rows this pass wrote can block dependents (cascade-skipped next pass), unblock others,
                // or complete a loop iteration: walk their frontier next.
                changed = next;
                continue;
            }

            if (enqueued > 0)
            {
                return;
            }

            changed = [];

            // Completion gate. O(1): an indexed existence query on the open rows. The common completion of a
            // long run stops here — a parked ForEach alone is an open row.
            if (await _runtimeStore.HasRunningOrPendingStepAsync(runId).ConfigureAwait(false))
            {
                return;
            }

            // Nothing is open: the run is about to close, so every check below reads the same full snapshot —
            // the one place the continuation reads every row of the run.
            var statuses = await _runtimeStore.GetStepStatusesAsync(runId).ConfigureAwait(false);
            if (await HasInFlightWorkAsync(runId, statuses).ConfigureAwait(false))
            {
                return;
            }

            // A step the planner reports ready on this snapshot (dependencies terminal, no status row, and
            // no ledger row — the in-flight check just returned for a dispatched one) or blocked (owed its
            // cascade-skip row) is work nobody has done yet. Do it here rather than close the run: the
            // continuation that unlocked it may still be between its write and its dispatch, or may have died
            // there. Under a cancel/timeout verdict ready steps are never dispatched, so they do not hold the
            // run open.
            var gate = _graphPlanner.Evaluate(flow, statuses);
            if (gate.BlockedStepKeys.Count > 0 || (termination is null && gate.ReadyStepKeys.Count > 0))
            {
                if (PassLimitReached(flow, pass))
                {
                    EngineLog.ContinuationPassLimitReached(_logger, runId, pass);
                    return;
                }

                carried = gate;
                continue;
            }

            // Single source of truth — same classifier that FlowRunRecoveryHostedService uses.
            termination ??= RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

            await CompleteActiveRunAsync(runId, termination).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// Classifies the dependents of <paramref name="changed"/> from a keyed read of their rows and their
    /// dependencies' rows, or the whole graph when <paramref name="evaluateFully"/> is set or a dependent
    /// cannot be enumerated by key.
    /// </summary>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="runId">The run being continued.</param>
    /// <param name="changed">Steps whose rows were written since the previous evaluation.</param>
    /// <param name="evaluateFully">Evaluate the whole graph from the full status map.</param>
    /// <returns>The readiness of the dependents (or, in full mode, of every step without a row).</returns>
    /// <remarks>
    /// <b>Complexity per step completion:</b> O(Σ fan-out · fan-in) to build the key set from
    /// <see cref="FlowTopology"/>, one keyed read of that many rows, and the planner's frontier evaluation over
    /// the dependents — no term in the run's status rows. The full-map fallback is taken for the trigger path
    /// (entry rows only), and for a key that does not resolve to a step or that a nested step names by a
    /// literal dotted <c>RunAfter</c> key — a manifest shape whose dependents exist once per scope instance and
    /// so cannot be listed by key.
    /// </remarks>
    private async Task<FlowGraphEvaluation> EvaluateChangesAsync(IFlowDefinition flow, Guid runId, List<string> changed, bool evaluateFully)
    {
        if (!evaluateFully)
        {
            if (changed.Count == 0)
            {
                return EmptyEvaluation;
            }

            var topology = FlowTopology.For(flow);
            var dependents = new HashSet<string>(StringComparer.Ordinal);
            var enumerable = true;
            // foreach on purpose (CLAUDE.md hot-path rule): per step completion, TryAddDependents mutates the set;
            // an All() would hide that side effect and allocate a closure. CodeQL cs/linq/missed-all dismissed.
            foreach (var changedKey in changed)
            {
                if (!topology.TryAddDependents(flow.Manifest.Steps, changedKey, dependents))
                {
                    enumerable = false;
                    break;
                }
            }

            if (enumerable)
            {
                if (dependents.Count == 0)
                {
                    return EmptyEvaluation;
                }

                var readKeys = new HashSet<string>(dependents, StringComparer.Ordinal);
                foreach (var dependent in dependents)
                {
                    FlowGraphPlanner.AddRuntimeDependencyKeys(flow, dependent, readKeys);
                }

                var frontier = await _runtimeStore.GetStepStatusesAsync(runId, readKeys).ConfigureAwait(false);
                return _graphPlanner.EvaluateFrontier(flow, dependents, frontier);
            }
        }

        var statuses = await _runtimeStore.GetStepStatusesAsync(runId).ConfigureAwait(false);
        return _graphPlanner.Evaluate(flow, statuses);
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
    /// <param name="skipped">Receives the key of every step this call skipped, for the next pass's frontier.</param>
    /// <remarks>O(1) store calls per blocked step: a claim, plus a skip write when the claim is won.</remarks>
    private async Task SkipBlockedStepsAsync(IExecutionContext ctx, IFlowDefinition flow, IReadOnlyList<string> blockedStepKeys, List<string> skipped)
    {
        foreach (var blockedStepKey in blockedStepKeys)
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
            skipped.Add(blockedStepKey);

            if (_observabilityOptions.EnableOpenTelemetry)
            {
                _telemetry.StepSkippedCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                    // Template key, not the runtime key: bounded metric cardinality. O(key length) per call, no
                    // allocation in steady state (MetricStepKey) — independent of the run's step count.
                    new KeyValuePair<string, object?>("step_key", MetricStepKey.For(blockedStepKey)),
                    new KeyValuePair<string, object?>("reason", "prerequisites_unmet"));
            }
        }
    }

    /// <summary>
    /// Advances the loops that the rows in <paramref name="changed"/> belong to — the innermost loop enclosing
    /// each key — plus <paramref name="selfLoopKey"/> when it is a loop step: admits the iterations a freed
    /// concurrency slot has room for, and settles a loop whose iterations have all finished.
    /// </summary>
    /// <param name="ctx">Execution context of the run.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="changed">Steps whose rows were written since the previous pass.</param>
    /// <param name="selfLoopKey">The completed step itself, considered when it is a scoped step; otherwise <see langword="null"/>.</param>
    /// <returns>The number of entry steps enqueued, and the loops this call moved to a terminal status.</returns>
    /// <remarks>
    /// <para>
    /// Only the innermost loop of a changed key can be advanced by it: an iteration of loop <c>L</c> settles
    /// only when one of its <i>direct</i> children changes, and a nested loop child counts once it settles
    /// itself — which is a change of the nested loop's key, handled next pass. The old candidate set — every
    /// Running scoped step in the run, read from the full status map — existed because a cascade-skip could
    /// finish a <i>sibling</i> loop's last iteration; that skip is now a changed key of its own, so its loop
    /// is the candidate.
    /// </para>
    /// <para>
    /// <b>Complexity per step completion:</b> O(changed keys) to group, then <see cref="AdvanceLoopAsync"/>
    /// per distinct loop — typically one.
    /// </para>
    /// </remarks>
    private async Task<(int Admitted, List<string>? Settled)> AdvanceLoopsAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        List<string> changed,
        string? selfLoopKey)
    {
        // Loop key → the iterations of it that a changed key belongs to; null means "consider the loop as a
        // whole" (the completed step is the loop itself).
        Dictionary<string, HashSet<int>?>? loops = null;
        // foreach on purpose (CLAUDE.md hot-path rule): per step completion, no closure; CodeQL cs/linq/missed-where dismissed.
        foreach (var changedKey in changed)
        {
            if (RuntimeStepKey.TryGetInnermostScope(changedKey, out var scope))
            {
                loops ??= new Dictionary<string, HashSet<int>?>(StringComparer.Ordinal);
                if (!loops.TryGetValue(scope.ScopeKey, out var touched))
                {
                    loops[scope.ScopeKey] = touched = [];
                }

                touched?.Add(scope.Index);
            }
        }

        if (selfLoopKey is not null && flow.Manifest.Steps.FindStep(selfLoopKey) is IScopedStep)
        {
            (loops ??= new Dictionary<string, HashSet<int>?>(StringComparer.Ordinal))[selfLoopKey] = null;
        }

        if (loops is null)
        {
            return (0, null);
        }

        var admitted = 0;
        List<string>? settled = null;
        foreach (var (loopKey, touched) in loops)
        {
            var advance = await AdvanceLoopAsync(ctx, flow, loopKey, touched).ConfigureAwait(false);
            admitted += advance.Admitted;
            if (advance.Settled)
            {
                (settled ??= []).Add(loopKey);
            }
        }

        return (admitted, settled);
    }

    /// <summary>
    /// Advances one loop parked on its barrier: settles it when every iteration finished, otherwise admits the
    /// iterations its <see cref="LoopStepMetadata.ConcurrencyLimit"/> now has room for.
    /// </summary>
    /// <param name="ctx">Execution context of the run.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="loopKey">Runtime key of the loop step.</param>
    /// <param name="touchedIterations">
    /// The iterations a changed row belongs to, or <see langword="null"/> when the loop step itself completed.
    /// </param>
    /// <returns>The entry steps enqueued, and whether the loop is now terminal (settled by this or another worker).</returns>
    /// <remarks>
    /// <para>
    /// <b>Derived state, no cursor.</b> Iteration <c>i</c> is <i>settled</i> when every direct child of
    /// <c>{loop}.{i}.</c> has a terminal row; it is <i>started</i> when an entry child has a row or a ledger
    /// row. Started iterations form a prefix <c>[0, A)</c> (admission only hands out the lowest unstarted
    /// indices), so with <c>S</c> settled iterations — all inside the prefix — the gate's target
    /// <c>min(n, A + limit − (A − S))</c> is <c>min(n, S + limit)</c>, and <c>A ≥ S</c>. Admission is
    /// therefore: read <c>S</c> (<see cref="IFlowRunRuntimeStore.CountSettledIterationsAsync"/>, one row), then
    /// dispatch the entries of every iteration in <c>[S, min(n, S + limit))</c> without an entry row; the
    /// already-started ones in that window are absorbed by the dispatch ledger. That is exactly the set the
    /// former full-map scan admitted, and the window is at most <c>limit</c> iterations wide.
    /// </para>
    /// <para>
    /// <b>Concurrency.</b> Two iterations that settle on two workers each read <c>S</c> after their own commit,
    /// so the later one counts both and admits both replacements; <c>S</c> only grows, so neither can admit
    /// past the limit. Two workers that both see <c>S = n</c> race the settle's compare-and-swap
    /// (<see cref="IFlowRunStore.TryCompleteRunningStepAsync"/>) and only the winner publishes the loop's
    /// <see cref="StepCompletedEvent"/>; both report the loop terminal so both evaluate its dependents, which
    /// the ledger deduplicates.
    /// </para>
    /// <para>
    /// <b>Complexity per step completion:</b> one output read and one keyed read of O(|body|) rows (the loop
    /// row, the touched iterations, the last iteration). It stops there unless a touched iteration just
    /// settled — a change that frees no slot can neither admit nor settle. A settle costs one
    /// <see cref="IFlowRunRuntimeStore.CountSettledIterationsAsync"/> (one row back; server-side a range read
    /// of the loop's rows, the remaining O(n) term, see the RFC's loop-row counters) and, before the last
    /// iteration has started, one keyed read of O(limit · |entries|) rows. An unbounded loop dispatched every
    /// iteration at fan-out, so it is counted only once its last iteration settled.
    /// </para>
    /// </remarks>
    private async Task<(int Admitted, bool Settled)> AdvanceLoopAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        string loopKey,
        HashSet<int>? touchedIterations)
    {
        if (flow.Manifest.Steps.FindStep(loopKey) is not IScopedStep { Steps.Count: > 0 } scoped)
        {
            return (0, false);
        }

        var runId = ctx.RunId;
        var output = await _outputsRepository.GetStepOutputAsync(runId, loopKey).ConfigureAwait(false);
        if (!LoopBarrier.TryReadIterationCount(output, out var iterations))
        {
            return (0, false);
        }

        var last = iterations - 1;
        var keys = new List<string> { loopKey };
        if (touchedIterations is not null)
        {
            // foreach on purpose (CLAUDE.md hot-path rule): per step completion, no closure; CodeQL cs/linq/missed-where dismissed.
            foreach (var index in touchedIterations)
            {
                if (index < iterations && index != last)
                {
                    LoopAdmission.AddIterationKeys(scoped, loopKey, index, keys);
                }
            }
        }

        if (iterations > 0)
        {
            LoopAdmission.AddIterationKeys(scoped, loopKey, last, keys);
        }

        var statuses = await _runtimeStore.GetStepStatusesAsync(runId, keys).ConfigureAwait(false);
        if (!statuses.TryGetValue(loopKey, out var loopStatus) || loopStatus != StepStatus.Running)
        {
            return (0, false);
        }

        if (touchedIterations is not null && !AnyIterationSettled(scoped, loopKey, touchedIterations, iterations, statuses))
        {
            return (0, false);
        }

        var limit = LoopAdmission.ConcurrencyLimitOf(scoped as StepMetadata);
        var lastSettled = iterations == 0 || LoopAdmission.IsIterationSettled(scoped, loopKey, last, statuses);
        int settledCount;
        if (lastSettled)
        {
            settledCount = await CountSettledIterationsAsync(runId, loopKey, iterations, scoped).ConfigureAwait(false);
            if (settledCount >= iterations)
            {
                return (0, await SettleLoopAsync(runId, loopKey, iterations).ConfigureAwait(false));
            }

            // Every iteration has started (the last one settled, so the prefix is complete): nothing to admit.
            return (0, false);
        }

        if (limit == LoopAdmission.Unbounded)
        {
            // The fan-out dispatched every iteration, and the loop cannot settle before its last one does.
            return (0, false);
        }

        var entries = LoopAdmission.EntrySteps(scoped);
        if (await IsIterationStartedAsync(runId, loopKey, last, entries, statuses).ConfigureAwait(false))
        {
            // The whole prefix has started: no iteration is left to admit.
            return (0, false);
        }

        settledCount = await CountSettledIterationsAsync(runId, loopKey, iterations, scoped).ConfigureAwait(false);
        var admitted = await AdmitWindowAsync(ctx, flow, loopKey, entries, iterations, settledCount, limit).ConfigureAwait(false);
        return (admitted, false);

        static bool AnyIterationSettled(
            IScopedStep scoped, string loopKey, HashSet<int> touched, int iterations, IReadOnlyDictionary<string, StepStatus> statuses)
        {
            foreach (var index in touched)
            {
                if (index < iterations && LoopAdmission.IsIterationSettled(scoped, loopKey, index, statuses))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Reads the number of settled iterations of <paramref name="loopKey"/> from the store. One row.</summary>
    private Task<int> CountSettledIterationsAsync(Guid runId, string loopKey, int iterations, IScopedStep scoped)
        => _runtimeStore.CountSettledIterationsAsync(runId, loopKey, iterations, scoped.Steps.Keys);

    /// <summary>
    /// Returns whether iteration <paramref name="index"/> has started: an entry child has a status row in
    /// <paramref name="statuses"/>, or — asked only when none has — a dispatch-ledger row.
    /// </summary>
    /// <remarks>O(|entries|): status lookups first, then at most one ledger point lookup per entry child.</remarks>
    private async Task<bool> IsIterationStartedAsync(
        Guid runId,
        string loopKey,
        int index,
        IReadOnlyList<KeyValuePair<string, StepMetadata>> entries,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        var prefix = $"{loopKey}.{index}.";
        for (var i = 0; i < entries.Count; i++)
        {
            if (statuses.ContainsKey(prefix + entries[i].Key))
            {
                return true;
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (await _runStore.IsStepDispatchedAsync(runId, prefix + entries[i].Key).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Dispatches the entry steps of every iteration in <c>[settled, min(iterations, settled + limit))</c> that
    /// has no entry status row, and returns how many dispatches this call won.
    /// </summary>
    /// <remarks>
    /// One keyed read of at most <c>limit · |entries|</c> rows, then one ledger insert per entry step of each
    /// iteration without a row — which absorbs the iterations already dispatched but not yet started, and a
    /// concurrent admission of the same iteration on another worker. Template inputs only:
    /// <c>DefaultStepExecutor</c> re-derives <c>__loopItem</c> / <c>__loopIndex</c> from the runtime key via
    /// <c>LoopScopeInputs</c>, which is also what every other late-dispatch path (signal resume, retry,
    /// recovery) relies on.
    /// </remarks>
    private async Task<int> AdmitWindowAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        string loopKey,
        IReadOnlyList<KeyValuePair<string, StepMetadata>> entries,
        int iterations,
        int settled,
        int limit)
    {
        var end = (int)Math.Min(iterations, (long)settled + limit);
        if (end <= settled || entries.Count == 0)
        {
            return 0;
        }

        var keys = new List<string>((end - settled) * entries.Count);
        for (var index = settled; index < end; index++)
        {
            var prefix = $"{loopKey}.{index}.";
            foreach (var entry in entries)
            {
                keys.Add(prefix + entry.Key);
            }
        }

        var statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId, keys).ConfigureAwait(false);
        var admitted = 0;
        for (var index = settled; index < end; index++)
        {
            var offset = (index - settled) * entries.Count;
            var started = false;
            for (var i = 0; i < entries.Count; i++)
            {
                if (statuses.ContainsKey(keys[offset + i]))
                {
                    started = true;
                    break;
                }
            }

            if (started)
            {
                continue;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var childStep = new StepInstance(keys[offset + i], entries[i].Value.Type)
                {
                    RunId = ctx.RunId,
                    PrincipalId = ctx.PrincipalId,
                    TriggerData = ctx.TriggerData,
                    TriggerHeaders = ctx.TriggerHeaders,
                    ScheduledTime = DateTimeOffset.UtcNow,
                    Inputs = new Dictionary<string, object?>(entries[i].Value.Inputs)
                };

                if (await TryScheduleStepAsync(ctx, flow, childStep, delay: null).ConfigureAwait(false))
                {
                    admitted++;
                    EngineLog.LoopIterationAdmitted(_logger, loopKey, index, childStep.Key);
                }
            }
        }

        return admitted;
    }

    /// <summary>
    /// Moves a loop whose iterations all settled from <see cref="StepStatus.Running"/> to
    /// <see cref="StepStatus.Succeeded"/> and, when this call won that compare-and-swap, publishes its
    /// deferred <see cref="StepCompletedEvent"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> either way: a lost swap means another writer already moved the row out of
    /// Running (a concurrent settle, or a run-control abandon), and every such writer writes a terminal status.
    /// </returns>
    /// <remarks>
    /// A loop step is the only step whose completion is decided outside its own handler, so its
    /// <c>step.completed</c> event is published here, at the point the loop is actually finished. O(1): one
    /// conditional write.
    /// </remarks>
    private async Task<bool> SettleLoopAsync(Guid runId, string loopKey, int iterations)
    {
        var won = await _runStore.TryCompleteRunningStepAsync(
            runId,
            loopKey,
            StepStatus.Succeeded.ToString(),
            JsonSerializer.Serialize(new { iterations }, LoopOutputJsonOptions),
            null).ConfigureAwait(false);

        if (won)
        {
            await PublishEventSafelyAsync(new StepCompletedEvent
            {
                RunId = runId,
                StepKey = loopKey,
                Status = StepStatus.Succeeded.ToString()
            }, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    private static readonly JsonSerializerOptions LoopOutputJsonOptions = new(JsonSerializerDefaults.Web);

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
            trace = await _whenEvaluator.EvaluateAsync(ctx, flow, metadata, stepKey).ConfigureAwait(false);
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
                // Template key, not the runtime key: bounded metric cardinality. O(key length) per call, no
                // allocation in steady state (MetricStepKey) — independent of the run's step count.
                new KeyValuePair<string, object?>("step_key", MetricStepKey.For(stepKey)),
                new KeyValuePair<string, object?>("reason", "when_false"));
        }

        return true;
    }
}
