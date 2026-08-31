using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Dispatch / control / event partial of <see cref="FlowOrchestratorEngine"/>:
/// dispatch-ledger guard, run completion, run-control termination resolution, and the
/// safe lifecycle-event publishers.
/// </summary>
public sealed partial class FlowOrchestratorEngine
{
    private async Task<bool> TryScheduleStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, TimeSpan? delay)
    {
        // Guard: dispatch ledger — prevents enqueueing the same step twice (recovery, retry, at-least-once queue).
        // Note (v1.22+): the runtime claim that used to also live here has moved to RunStepAsync entry.
        // Schedule no longer claims because schedule-time claims break under at-least-once message delivery
        // when one enqueue is broadcast to multiple consumers (Service Bus topic without SQL filter). Now:
        //   - Schedule = "an execution attempt is queued" (idempotent ledger)
        //   - Execute  = "this worker is running it" (atomic claim at top of RunStepAsync)
        // The two responsibilities used to be conflated; splitting them makes broadcast delivery correct.
        if (!await _runStore.TryRecordDispatchAsync(ctx.RunId, step.Key).ConfigureAwait(false))
        {
            return false;
        }

        string? jobId;
        if (delay.HasValue)
        {
            step.ScheduledTime = DateTimeOffset.UtcNow + delay.Value;
            jobId = await _dispatcher.ScheduleStepAsync(ctx, flow, step, delay.Value).ConfigureAwait(false);
        }
        else
        {
            jobId = await _dispatcher.EnqueueStepAsync(ctx, flow, step).ConfigureAwait(false);
        }

        // Best-effort: record the runtime job/message ID for observability.
        if (jobId is not null)
        {
            try
            {
                await _runStore.AnnotateDispatchAsync(ctx.RunId, step.Key, jobId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EngineLog.DispatchAnnotateFailed(_logger, ex, step.Key);
            }
        }

        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the run still has a step that is in flight, claimed by a
    /// worker, or dispatched-but-not-yet-picked-up — i.e. work that could still advance or complete
    /// the run. Used to gate run completion and to keep the periodic timeout sweep from latching a run
    /// that is not actually stuck.
    /// </summary>
    private async Task<bool> HasInFlightWorkAsync(Guid runId)
    {
        if (_runtimeStore is null)
        {
            return false;
        }

        var statuses = await _runtimeStore.GetStepStatusesAsync(runId).ConfigureAwait(false);
        if (statuses.Values.Any(IsInFlight))
        {
            return true;
        }

        var claimed = await _runtimeStore.GetClaimedStepKeysAsync(runId).ConfigureAwait(false);
        if (claimed.Except(statuses.Keys, StringComparer.Ordinal).Any())
        {
            return true;
        }

        // Dispatch ledger check — closes a CI-only race observed in the v1.23.0 publish run
        // (HappyPathTests.LinearFlow_runs_to_completion: Expected 3 steps, got 2). A step
        // can have been dispatched (TryRecordDispatchAsync = true, EnqueueStepAsync queued
        // the work) but not yet picked up by the consumer — in that window neither the step
        // status nor the claim ledger reflects it, so the prior two checks pass and the
        // engine completes the run prematurely. Under CI CPU contention the gap between
        // dispatch and claim widens enough for this to fire. Guarding against it makes
        // termination strictly safer with no production downside.
        var dispatched = await _runStore.GetDispatchedStepKeysAsync(runId).ConfigureAwait(false);
        if (dispatched.Except(statuses.Keys, StringComparer.Ordinal).Any())
        {
            return true;
        }

        return false;
    }

    private async Task TryCompleteRunAsync(Guid runId, string status)
    {
        if (await HasInFlightWorkAsync(runId).ConfigureAwait(false))
        {
            return;
        }

        // Idempotent, race-safe completion: only the call that actually transitions the run out of
        // Running publishes the lifecycle event and bumps telemetry. Collapses the race between the
        // graph continuation and the periodic timeout sweep (single- or multi-instance) without a
        // distributed lock — the TOCTOU between HasInFlightWorkAsync and the write is closed by the
        // guarded transition inside CompleteRunIfActiveAsync.
        if (!await _runStore.CompleteRunIfActiveAsync(runId, status).ConfigureAwait(false))
        {
            return;
        }

        if (_observabilityOptions.EnableOpenTelemetry)
        {
            _telemetry.RunCompletedCounter.Add(
                1,
                new KeyValuePair<string, object?>("status", status));
        }

        await PublishEventSafelyAsync(new RunCompletedEvent
        {
            RunId = runId,
            Status = status
        }, default).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes <paramref name="evt"/> through <see cref="IFlowEventNotifier"/>, swallowing every
    /// exception (including <see cref="OperationCanceledException"/>). Telemetry must NEVER abort
    /// a flow — a misbehaving notifier (slow channel, disposed broadcaster, transient backplane
    /// error, internally-cancelled task) is logged and ignored. The CodeQL <c>when</c> filter on
    /// the catch is deliberately a tautology so the CWE-396 / cs/catch-of-all-exceptions analyzer
    /// stays quiet without weakening the documented isolation contract.
    /// </summary>
    private async ValueTask PublishEventSafelyAsync(FlowLifecycleEvent evt, CancellationToken ct)
    {
        try
        {
            await _eventNotifier.PublishAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not null)
        {
            EngineLog.EventNotifierFailed(_logger, ex, evt.Type);
        }
    }

    private async Task<string?> ResolveTerminationStatusAsync(Guid runId)
    {
        if (_runControlStore is null)
        {
            return null;
        }

        var control = await _runControlStore.GetRunControlAsync(runId).ConfigureAwait(false);
        if (control is null)
        {
            return null;
        }

        if (control.TimedOutAtUtc is not null)
        {
            return "TimedOut";
        }

        // A genuine user cancellation (CancelRequested set while never timed out) wins over a merely
        // lapsed deadline — otherwise a cancelled run whose deadline also passed would be mislabelled
        // TimedOut, and a subsequent retry would then clear the cancel latch. Checked BEFORE the
        // deadline branch so cancel intent is honoured.
        if (control.CancelRequested)
        {
            return "Cancelled";
        }

        if (control.TimeoutAtUtc is not null && DateTimeOffset.UtcNow >= control.TimeoutAtUtc.Value)
        {
            await _runControlStore.MarkTimedOutAsync(runId, "Run timed out before scheduling next step.").ConfigureAwait(false);
            return "TimedOut";
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task EnforceDueTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        if (_runControlStore is null)
        {
            return;
        }

        var activeRuns = await _runStore.GetActiveRunsAsync().ConfigureAwait(false);
        if (activeRuns.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var run in activeRuns)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var control = await _runControlStore.GetRunControlAsync(run.Id).ConfigureAwait(false);
                if (control is null)
                {
                    continue;
                }

                // Resolve the run-control verdict, mirroring ResolveTerminationStatusAsync's precedence
                // so the sweep can never disagree with the dispatch-time gate:
                //   1. an already-latched timeout is TimedOut — the cancel latch MarkTimedOutAsync also
                //      sets must never surface as a user cancellation,
                //   2. a genuine user cancel is Cancelled,
                //   3. a merely lapsed deadline is TimedOut, and this sweep is the one that latches it.
                string? terminalStatus;
                var mustLatchTimeout = false;
                if (control.TimedOutAtUtc is not null)
                {
                    terminalStatus = "TimedOut";
                }
                else if (control.CancelRequested)
                {
                    terminalStatus = "Cancelled";
                }
                else if (control.TimeoutAtUtc is not null && now >= control.TimeoutAtUtc.Value)
                {
                    terminalStatus = "TimedOut";
                    mustLatchTimeout = true;
                }
                else
                {
                    continue;
                }

                // Only enforce on genuinely stuck runs. A run with a live/queued step is not stuck —
                // its deadline is enforced by the dispatch-time gate on that step's next dispatch. If
                // we latched TimedOut here while the final in-flight step then completed successfully,
                // the graph continuation (which classifies terminal status from step statuses, not the
                // control record) would complete the run as Succeeded, leaving the control record and
                // the run status inconsistent. Skipping in-flight runs avoids that entirely.
                var stuck = !await HasInFlightWorkAsync(run.Id).ConfigureAwait(false);

                // The "in-flight" work may be parked rather than live: a WaitForSignal registered with
                // no timeoutSeconds parks for 24 h, and a long-interval poll for its whole interval.
                // Such a step only observes the termination gate on its next wake-up, so a cancelled or
                // timed-out run would otherwise sit Running for that long. Force-close it when every
                // remaining step is provably parked.
                if (!stuck && !await TryForceCloseParkedRunAsync(run, terminalStatus).ConfigureAwait(false))
                {
                    continue;
                }

                // A stuck run the user already cancelled must be finalised as Cancelled, not mislabelled
                // TimedOut, and must NOT latch the timeout — otherwise a later retry's ExtendDeadlineAsync
                // would clear the cancel.
                if (mustLatchTimeout)
                {
                    await _runControlStore.MarkTimedOutAsync(run.Id, "Run exceeded its timeout deadline.").ConfigureAwait(false);
                }

                await TryCompleteRunAsync(run.Id, terminalStatus).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-run isolation: a single bad run must never abort the whole sweep.
                EngineLog.TimeoutEnforcementRunFailed(_logger, ex, run.Id);
            }
        }
    }

    /// <summary>
    /// Resolves a run whose only remaining work is <i>parked</i> — every non-terminal step is either a
    /// <see cref="StepStatus.Pending"/> poll waiting on its own wake-up timer or a scoped step parked on
    /// its loop barrier — by recording each of those steps <see cref="StepStatus.Skipped"/> so the run
    /// can be completed with <paramref name="terminalStatus"/>.
    /// </summary>
    /// <param name="run">The active run being force-closed.</param>
    /// <param name="terminalStatus">The run-control verdict (<c>Cancelled</c> or <c>TimedOut</c>).</param>
    /// <returns>
    /// <see langword="true"/> when the run was resolved and may now be completed;
    /// <see langword="false"/> when anything could still advance it and the sweep must leave it alone.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Without this, a run whose only live work is a <c>WaitForSignal</c> registered without a
    /// <c>timeoutSeconds</c> stays <c>Running</c> for the handler's 24-hour park interval after a cancel,
    /// because the termination gate is only consulted when the step next wakes. The same applies to any
    /// long-interval <c>PollableStepHandler</c>.
    /// </para>
    /// <para>
    /// The guard is deliberately conservative and refuses whenever anything could still make progress: a
    /// step that is genuinely <see cref="StepStatus.Running"/> (a worker is inside its handler), a claimed
    /// key with no status row yet (a worker between claim and start), or a dispatched key with no status
    /// row yet (queued but not picked up). A <see cref="StepStatus.Running"/> scoped step only counts as
    /// parked once its fan-out output is persisted, which is what separates a barrier-parked loop from a
    /// <c>ForEach</c> that is still mid-fan-out or is being re-executed by a retry.
    /// </para>
    /// <para>
    /// A parked step that wakes between this decision and the completion write is harmless: its next
    /// execution hits the termination gate at the top of <c>RunStepAsync</c>, records the same
    /// <see cref="StepStatus.Skipped"/> outcome, and its <c>TryCompleteRunAsync</c> is a no-op because
    /// <c>CompleteRunIfActiveAsync</c> only transitions a still-<c>Running</c> run.
    /// </para>
    /// </remarks>
    private async Task<bool> TryForceCloseParkedRunAsync(FlowRunRecord run, string terminalStatus)
    {
        if (_runtimeStore is null)
        {
            return false;
        }

        var statuses = await _runtimeStore.GetStepStatusesAsync(run.Id).ConfigureAwait(false);
        if (statuses.Count == 0)
        {
            return false;
        }

        List<string>? parked = null;
        List<string>? runningKeys = null;
        foreach (var entry in statuses)
        {
            if (entry.Value == StepStatus.Pending)
            {
                (parked ??= []).Add(entry.Key);
            }
            else if (entry.Value == StepStatus.Running)
            {
                (runningKeys ??= []).Add(entry.Key);
            }
        }

        // Nothing is waiting on a wake-up timer, so this is not the stuck-forever shape: leave the run
        // to the ordinary in-flight guard rather than force-closing work that can still complete.
        if (parked is null)
        {
            return false;
        }

        // A claimed or dispatched key with no status row is a worker that is about to record one.
        var claimed = await _runtimeStore.GetClaimedStepKeysAsync(run.Id).ConfigureAwait(false);
        if (claimed.Except(statuses.Keys, StringComparer.Ordinal).Any())
        {
            return false;
        }

        var dispatched = await _runStore.GetDispatchedStepKeysAsync(run.Id).ConfigureAwait(false);
        if (dispatched.Except(statuses.Keys, StringComparer.Ordinal).Any())
        {
            return false;
        }

        if (runningKeys is not null)
        {
            var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
            var flow = flows?.FindById(run.FlowId);
            if (flow is null)
            {
                return false;
            }

            foreach (var runningKey in runningKeys)
            {
                if (!await IsBarrierParkedLoopAsync(flow, run.Id, runningKey).ConfigureAwait(false))
                {
                    return false;
                }

                parked.Add(runningKey);
            }
        }

        foreach (var stepKey in parked)
        {
            try
            {
                await _runStore.RecordStepCompleteAsync(
                    run.Id,
                    stepKey,
                    StepStatus.Skipped.ToString(),
                    null,
                    $"Run is {terminalStatus}.").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EngineLog.StepSkipTrackingFailed(_logger, ex, stepKey);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="runningKey"/> is a scoped step parked on its
    /// completion barrier rather than a <c>ForEach</c> that is still fanning out (or being re-executed by
    /// a retry): it is scoped in the manifest and the fan-out output the barrier reads its iteration count
    /// back from is already persisted.
    /// </summary>
    /// <param name="flow">The flow definition owning the run.</param>
    /// <param name="runId">The run being inspected.</param>
    /// <param name="runningKey">Runtime key of a step currently in <see cref="StepStatus.Running"/>.</param>
    private async Task<bool> IsBarrierParkedLoopAsync(IFlowDefinition flow, Guid runId, string runningKey)
    {
        if (flow.Manifest.Steps.FindStep(runningKey) is not IScopedStep)
        {
            return false;
        }

        var output = await _outputsRepository.GetStepOutputAsync(runId, runningKey).ConfigureAwait(false);
        return LoopBarrier.TryReadIterationCount(output, out _);
    }

    /// <summary>
    /// Persists a lifecycle event via <see cref="IOutputsRepository.RecordEventAsync"/>, swallowing
    /// every exception (including <see cref="OperationCanceledException"/>). Event persistence is
    /// best-effort observability — a storage failure or internally-cancelled write must NEVER abort
    /// a flow that is otherwise progressing. The CodeQL <c>when</c> filter on the catch is
    /// deliberately a tautology so the CWE-396 / cs/catch-of-all-exceptions analyzer stays quiet
    /// without weakening the documented isolation contract.
    /// </summary>
    private async ValueTask RecordEventAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance step,
        string type,
        string? message,
        string? stepKey = null)
    {
        if (!_observabilityOptions.EnableEventPersistence)
        {
            return;
        }

        try
        {
            await _outputsRepository.RecordEventAsync(
                ctx,
                flow,
                step,
                new FlowEvent
                {
                    Type = type,
                    Message = message,
                    StepKey = stepKey
                }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not null)
        {
            EngineLog.EventPersistenceFailed(_logger, ex, type);
        }
    }
}
