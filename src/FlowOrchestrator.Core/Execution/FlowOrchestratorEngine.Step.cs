using System.Diagnostics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Per-step execution partial of <see cref="FlowOrchestratorEngine"/>:
/// <see cref="RunStepAsync"/>, <see cref="RetryStepAsync"/>, claim guards, control-store
/// termination check, handler dispatch, and dispatch-hint fan-out.
/// </summary>
internal sealed partial class FlowOrchestratorEngine
{
    /// <inheritdoc/>
    public async ValueTask<object?> RunStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, CancellationToken ct = default)
    {
        using var activity = _observabilityOptions.EnableOpenTelemetry
            ? _telemetry.ActivitySource.StartActivity("flow.step", ActivityKind.Internal)
            : null;

        await EnsureTriggerDataAsync(ctx).ConfigureAwait(false);

        // IStepInstance is itself an IExecutionContext, so a handler can read the trigger payload off
        // the step. Runtimes that rebuild the step from a slim envelope (Service Bus, and the InMemory
        // runtime's delayed schedules, which no longer retain the payload for the whole delay — #189)
        // hand over a step without it; fill it from the context the engine just rehydrated so every
        // runtime presents the same step to the handler.
        step.TriggerData ??= ctx.TriggerData;
        step.TriggerHeaders ??= ctx.TriggerHeaders;
        _contextAccessor.CurrentContext = ctx;

        using var _scope = EngineLogScope.Begin(_logger, ctx.RunId, flow.Id, step.Key, attempt: null, flowName: flow.GetType().Name, stepType: step.Type);

        activity?.SetTag("flow.id", flow.Id.ToString());
        activity?.SetTag("flow.name", flow.GetType().Name);
        activity?.SetTag("run.id", ctx.RunId.ToString());
        activity?.SetTag("step.key", step.Key);
        activity?.SetTag("step.type", step.Type);

        var queueDelayMs = Math.Max(0d, (DateTimeOffset.UtcNow - step.ScheduledTime).TotalMilliseconds);
        if (_observabilityOptions.EnableOpenTelemetry)
        {
            _telemetry.QueueDelayMs.Record(
                queueDelayMs,
                new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                new KeyValuePair<string, object?>("step_key", MetricStepKey.For(step.Key)));
        }

        try
        {
            // Mixed-version window (rolling deploy): a step dispatched by a replica running a newer
            // manifest can be delivered to a replica that has not restarted yet. Its manifest does not
            // contain the step, and before 2.0 the executor recorded it Skipped ("Step metadata not
            // found.") — a terminal row no later version could undo, which then cascade-skipped every
            // dependent and could close the run Skipped. Decide BEFORE the claim, so a hand-back leaves
            // nothing behind. Cost per step execution, independent of the run's size: one allocation-free
            // FindStep — a dictionary hit for a top-level key, O(key length) for a loop child — the same
            // lookup the executor repeats. The flow-store read happens only on the miss.
            if (flow.Manifest.Steps.FindStep(step.Key) is null
                && await TryHandBackStepUnknownToManifestAsync(ctx, flow, step).ConfigureAwait(false))
            {
                activity?.SetTag("flow.step.handed_back", true);
                return null;
            }

            // Execute-time claim guard (v1.22+). Atomic INSERT into FlowStepClaims; only the first
            // worker to call this for (runId, stepKey) wins, the rest exit silently. Critical for
            // at-least-once delivery models where one enqueued message can reach multiple
            // consumers (Service Bus topic-broadcast, redelivery after a worker timeout, etc.).
            // Pre-1.22 the claim was at schedule time which assumed 1:1 enqueue→execute — broken
            // under broadcast. Released by RetryStepAsync and the Pending re-schedule path so the
            // SAME step can claim again on a fresh attempt.
            //
            // 2.0 (RFC C1): with the lease keeper the claim is a lease that expires unless renewed, and a lease
            // whose worker died is taken over here — before, a worker killed mid-handler left a claim that made
            // every redelivery exit, and the run stayed Running for ever. Every write this attempt makes from
            // now on is fenced by the lease, so an attempt that lost it cannot overwrite its successor's work.
            // O(1) per step execution: one claim statement, one keeper insert.
            StepLease? lease = null;
            bool claimed;
            if (_leaseKeeper is { Duration: { } leaseDuration } keeper)
            {
                lease = await _runtimeStore.TryAcquireStepLeaseAsync(ctx.RunId, step.Key, keeper.OwnerId, leaseDuration).ConfigureAwait(false);
                claimed = lease is not null;
            }
            else
            {
                claimed = await _runtimeStore.TryClaimStepAsync(ctx.RunId, step.Key).ConfigureAwait(false);
            }

            if (!claimed)
            {
                activity?.SetTag("flow.step.claim_lost", true);
                if (_observabilityOptions.EnableOpenTelemetry)
                {
                    _telemetry.StepClaimLostCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                        new KeyValuePair<string, object?>("step_key", MetricStepKey.For(step.Key)));
                }

                return null;
            }

            // Renewed until this attempt is over — the handler, its completion write and the continuation.
            using var leaseHandle = lease is null ? null : _leaseKeeper!.Track(lease);
            if (lease is { Fence: > 1 })
            {
                activity?.SetTag("flow.step.lease_taken_over", true);
                EngineLog.StepLeaseTakenOver(_logger, step.Key, lease.Fence);
            }

            // From here until the start write succeeds the step is claimed with no status row. A failure
            // in that window must not leave it so: every later attempt would lose the claim and exit, and
            // the completion gate would count the key as in-flight work for ever (RFC 2.0 S1b). Release
            // the claim and the ledger row, then rethrow so the runtime retries the job — the step is back
            // to "ready, never dispatched", which recovery re-dispatches if the runtime gives up.
            string? controlStatus;
            try
            {
                controlStatus = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Catch-all-and-rethrow: never swallowed, cancellation included.
                await ReleaseAfterFailedStartAsync(ctx.RunId, step.Key, lease).ConfigureAwait(false);
                throw;
            }

            if (controlStatus is not null)
            {
                await RecordSkippedCurrentStepAsync(ctx, flow, step, controlStatus).ConfigureAwait(false);
                await AbandonEnclosingLoopsAsync(ctx, flow, step, controlStatus).ConfigureAwait(false);
                await TryCompleteRunAsync(ctx.RunId, controlStatus).ConfigureAwait(false);
                return null;
            }

            // No IOutputsRepository.SaveStepInputAsync here: DefaultStepExecutor writes the same
            // "{key}:input" row after resolving expressions, so this write only ever persisted the
            // unresolved inputs to be overwritten a moment later — one wasted MERGE and serialisation
            // per step (#189). The unresolved form is still recorded, on FlowSteps.InputJson below.
            //
            // Before 2.0 a failed start write was only logged and the handler ran anyway: the completion
            // UPDATE then matched no row, and the claim with no status row stranded the run (S1b).
            try
            {
                var inputJson = SafeSerialize(step.Inputs);
                await _runStore.RecordStepStartAsync(ctx.RunId, step.Key, step.Type, inputJson, ctx.JobId, _serverId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Catch-all-and-rethrow, as above: logged, released, never swallowed.
                EngineLog.StepStartTrackingFailed(_logger, ex);
                await ReleaseAfterFailedStartAsync(ctx.RunId, step.Key, lease).ConfigureAwait(false);
                throw;
            }

            var stepExecutionStart = Stopwatch.GetTimestamp();
            IStepResult result;
            Exception? handlerException = null;
            try
            {
                result = await _stepExecutor.ExecuteAsync(ctx, flow, step).ConfigureAwait(false);

                // A renewal already found the lease taken over: the step's new owner writes its output. The
                // fenced completion below rejects this attempt in any case; skipping the output write keeps
                // it from overwriting the new owner's output in the window before that.
                if (leaseHandle is { IsLost: true })
                {
                    EngineLog.StepLeaseLost(_logger, step.Key, lease!.Fence);
                    activity?.SetTag("flow.step.lease_lost", true);
                    return null;
                }

                await _outputsRepository.SaveStepOutputAsync(ctx, flow, step, result).ConfigureAwait(false);
            }
            // Handler boundary: any exception (including OperationCanceledException) is captured
            // and converted to a Failed StepResult. Step execution must never propagate exceptions
            // out of RunStepAsync — the engine catches everything and persists the failure.
            catch (Exception ex)
            {
                handlerException = ex;
                EngineLog.StepExecutionFailed(_logger, ex, step.Key);
                result = new StepResult
                {
                    Key = step.Key,
                    Status = StepStatus.Failed,
                    FailedReason = ex.ToString(),
                    ReThrow = false
                };
            }

            // Mark the step activity as Error so APMs treat the span as a failure even when
            // the handler exception is swallowed and translated into a Failed StepResult.
            if (result.Status == StepStatus.Failed)
            {
                if (handlerException is not null)
                {
                    activity?.RecordError(handlerException);
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Error, result.FailedReason);
                }
            }

            // A Running result under the graph runtime is a scoped step parked on its LoopBarrier —
            // not a completion. RecordStepStartAsync already stamped the row Running, so skipping the
            // completion write keeps CompletedAt null until the barrier settles the step for real
            // (the settle pass writes Succeeded + the loop output). The barrier reads the iteration
            // count from IOutputsRepository (SaveStepOutputAsync above), not from this row.
            var isBarrierParked = result.Status == StepStatus.Running;
            if (!isBarrierParked)
            {
                var recorded = true;
                try
                {
                    var outputJson = SafeSerialize(result.Result);
                    if (lease is not null)
                    {
                        recorded = await _runtimeStore.TryRecordLeasedStepCompleteAsync(lease, result.Status.ToString(), outputJson, result.FailedReason)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _runStore.RecordStepCompleteAsync(ctx.RunId, step.Key, result.Status.ToString(), outputJson, result.FailedReason)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The completion is not durable. With a lease, the step stays Running under a lease that
                    // stops being renewed once this attempt is over, so the expired-lease sweep runs it again
                    // (RFC 2.0 S3) instead of the run waiting on it for ever.
                    EngineLog.StepCompletionTrackingFailed(_logger, ex);
                }

                if (!recorded)
                {
                    // Another worker took the expired lease over and owns the step now: its attempt records the
                    // outcome and runs the continuation. Doing either here would race it.
                    EngineLog.StepLeaseLost(_logger, step.Key, lease!.Fence);
                    activity?.SetTag("flow.step.lease_lost", true);
                    return null;
                }
            }
            else if (lease is not null)
            {
                // Parked on its loop barrier: the handler has returned and nothing renews the lease any more, yet
                // the step must not be run again (that would fan the loop out a second time). Make it permanent;
                // the barrier settle records the step's terminal status.
                try
                {
                    if (!await _runtimeStore.PinStepLeaseAsync(lease).ConfigureAwait(false))
                    {
                        EngineLog.StepLeaseLost(_logger, step.Key, lease.Fence);
                        activity?.SetTag("flow.step.lease_lost", true);
                        return null;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    EngineLog.StepLeasePinFailed(_logger, ex, step.Key);
                }
            }

            var stepExecutionMs = Stopwatch.GetElapsedTime(stepExecutionStart).TotalMilliseconds;
            if (_observabilityOptions.EnableOpenTelemetry)
            {
                _telemetry.StepDurationMs.Record(
                    stepExecutionMs,
                    new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                    new KeyValuePair<string, object?>("step_key", MetricStepKey.For(step.Key)),
                    new KeyValuePair<string, object?>("status", result.Status.ToString()));

                _telemetry.StepCompletedCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                    new KeyValuePair<string, object?>("status", result.Status.ToString()));
            }

            // Pending / Running are non-terminal — only publish step.completed for terminal statuses.
            if (result.Status is not (StepStatus.Pending or StepStatus.Running))
            {
                await PublishEventSafelyAsync(new StepCompletedEvent
                {
                    RunId = ctx.RunId,
                    StepKey = step.Key,
                    Status = result.Status.ToString(),
                    FailedReason = result.Status == StepStatus.Failed ? result.FailedReason : null
                }, ct).ConfigureAwait(false);
            }

            if (result.Status == StepStatus.Pending)
            {
                // A step returning Pending means a poll iteration completed without satisfying the
                // condition — count it so operators can see how aggressive a flow's polling is.
                if (_observabilityOptions.EnableOpenTelemetry)
                {
                    _telemetry.StepPollAttemptsCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                        new KeyValuePair<string, object?>("step_key", MetricStepKey.For(step.Key)));
                }

                var controlAfterPending = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);
                if (controlAfterPending is not null)
                {
                    // The run was terminated while this poll iteration was executing — i.e. the
                    // cancel/timeout landed after the entry gate but before the handler returned,
                    // which for a WaitForSignal or a polling step is the whole fetch duration.
                    // Returning here without a reschedule strands the step in Pending with a live
                    // dispatch row and nothing queued, and a Pending step keeps HasInFlightWorkAsync
                    // true forever, so neither TryCompleteRunAsync below nor the periodic timeout
                    // sweep could ever close the run again (only a host restart would). Record the
                    // same Skipped outcome the entry gate produces — the step row already exists, so
                    // this is a completion write rather than a start+complete pair.
                    try
                    {
                        if (lease is not null)
                        {
                            await _runtimeStore.TryRecordLeasedStepCompleteAsync(
                                lease,
                                StepStatus.Skipped.ToString(),
                                null,
                                $"Run is {controlAfterPending}.").ConfigureAwait(false);
                        }
                        else
                        {
                            await _runStore.RecordStepCompleteAsync(
                                ctx.RunId,
                                step.Key,
                                StepStatus.Skipped.ToString(),
                                null,
                                $"Run is {controlAfterPending}.").ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        EngineLog.StepSkipTrackingFailed(_logger, ex, step.Key);
                    }

                    await AbandonEnclosingLoopsAsync(ctx, flow, step, controlAfterPending).ConfigureAwait(false);
                    await TryCompleteRunAsync(ctx.RunId, controlAfterPending).ConfigureAwait(false);
                    return result.Result;
                }

                var retryDelay = result.DelayNextStep ?? TimeSpan.FromSeconds(10);

                // Hand the step from "claimed by this invocation" to "queued for the next attempt"
                // with the dispatch ledger continuously covering it. Order matters (#190, race 2):
                //   1. ReleaseDispatchAsync  — clear this attempt's FlowStepDispatches row
                //   2. TryRecordDispatchAsync — reserve the row for the next attempt, claim still held
                //   3. ReleaseStepClaimAsync — the next attempt (safety net or signal resume) may claim
                //   4. dispatch               — hand the next attempt to the runtime
                // Pre-1.22 only the dispatch was released; the claim leaked across attempts and a
                // Pending poll silently no-op'd (the v2-runtime-claim-leak known issue). Up to v1.32
                // the claim was released BEFORE the ledger row was re-recorded, so for a moment the
                // step was neither claimed nor reserved. Reserving first means it never is.
                // Releasing the claim last of all is NOT safe — a fast dispatcher can fire the scheduled
                // attempt before the release, and then it is that attempt that loses the claim.
                //
                // Steps 1–2 now run while the claim is held, so a storage fault in either must not leave
                // it held: the runtime's retry of this job would lose TryClaimStepAsync and exit, recovery
                // skips a Pending step as in-flight, a signal nudge would lose the claim too — the step
                // would never run again. On failure, release the claim, re-assert the ledger, rethrow.
                bool reserved;
                try
                {
                    await _runStore.ReleaseDispatchAsync(ctx.RunId, step.Key).ConfigureAwait(false);
                    reserved = await _runStore.TryRecordDispatchAsync(ctx.RunId, step.Key).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Catch-all-and-rethrow, as on the dispatch failure below: never swallowed.
                    await ReleaseClaimAfterFailedHandOffAsync(ctx.RunId, step.Key, lease).ConfigureAwait(false);
                    await ReassertDispatchAfterFailedRescheduleAsync(ctx.RunId, step.Key).ConfigureAwait(false);
                    throw;
                }

                if (!reserved)
                {
                    // Another party (recovery, a retry) recorded the row inside the release/record gap.
                    // Its job may already have run and lost the claim this invocation still holds, so
                    // skipping the reschedule here could leave the step unclaimed with nothing queued —
                    // and recovery skips keys that already carry a dispatch row. Dispatch anyway: a
                    // duplicate attempt is absorbed by the execution claim, a missing one strands the step.
                    EngineLog.PendingRescheduleLedgerTaken(_logger, step.Key);
                }

                await ReleaseClaimAsync(ctx.RunId, step.Key, lease).ConfigureAwait(false);

                // Reschedule the poll. If the dispatcher throws (transient queue/broker error)
                // after the claim was released, the step must not be left status Pending, unclaimed
                // and unscheduled — stranded until a host restart, and even then the recovery service
                // treats a Pending step as in-flight and never re-enqueues it. The ledger row reserved
                // above normally survives the throw; ReassertDispatchAfterFailedRescheduleAsync is
                // idempotent and re-asserts it anyway, so the dispatch-ledger invariant (a non-terminal
                // step is always either claimed, dispatched, or has queued work) holds and the
                // runtime's own job-retry can drive the next attempt. The claim deliberately stays
                // released so that retried attempt can re-claim.
                try
                {
                    await DispatchReservedStepAsync(ctx, flow, step, retryDelay).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Catch-all-and-rethrow: every failure mode (including OperationCanceledException)
                    // must re-assert the ledger before propagating; the exception is never swallowed.
                    await ReassertDispatchAfterFailedRescheduleAsync(ctx.RunId, step.Key).ConfigureAwait(false);
                    throw;
                }

                await ResumeIfSignalLandedWhileClaimedAsync(ctx, flow, step).ConfigureAwait(false);
                return result.Result;
            }

            // Dispatch dynamic child steps declared by the handler (e.g. ForEach iterations).
            // Validation: hints must NOT target static DAG steps — only dynamic fan-out is allowed.
            if (result.DispatchHint?.Spawn is { Count: > 0 } hintChildren)
            {
                foreach (var child in hintChildren)
                {
                    // Reject hints that target a STATIC DAG step (one the planner dispatches on
                    // its own) — but NOT loop fan-out children. A ForEach child key carries a
                    // runtime iteration index ("{loop}.{index}.{child}"); StepCollection.FindStep
                    // deliberately resolves such keys to the loop's template child, so without the
                    // IsDynamicFanOutKey exemption this guard threw on every legitimate iteration,
                    // leaving the loop step Succeeded while its children were never dispatched and
                    // the downstream step never ran (the run hung forever).
                    if (!IsDynamicFanOutKey(child.StepKey) && flow.Manifest.Steps.FindStep(child.StepKey) is not null)
                    {
                        throw new InvalidOperationException(
                            $"DispatchHint targeted static DAG step '{child.StepKey}'. " +
                            "Hints are reserved for dynamic fan-out. Use runAfter for static dependencies.");
                    }

                    var childStep = new StepInstance(child.StepKey, child.StepType)
                    {
                        RunId = ctx.RunId,
                        PrincipalId = ctx.PrincipalId,
                        TriggerData = ctx.TriggerData,
                        TriggerHeaders = ctx.TriggerHeaders,
                        ScheduledTime = DateTimeOffset.UtcNow + (child.Delay ?? TimeSpan.Zero),
                        Inputs = new Dictionary<string, object?>(child.Inputs)
                    };

                    await TryScheduleStepAsync(ctx, flow, childStep, child.Delay).ConfigureAwait(false);
                }
            }

            await RunGraphContinuationAsync(ctx, flow, step, result).ConfigureAwait(false);

            if (result.ReThrow)
            {
                throw new InvalidOperationException(result.FailedReason ?? "Step execution requested rethrow.");
            }

            return result.Result;
        }
        catch (Exception ex)
        {
            // Engine-level failure outside the handler (storage, dispatch, continuation). The
            // inner catch already recorded handler exceptions; this guards everything else.
            activity?.RecordError(ex);
            throw;
        }
        finally
        {
            _contextAccessor.CurrentContext = null;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="stepKey"/> is a dynamic loop
    /// fan-out child — i.e. it carries a runtime iteration-index segment shaped
    /// <c>{loopKey}.{index}.{childKey}</c>. Such keys are produced by
    /// <see cref="ForEachStepHandler"/> and must NOT trip the static-DAG dispatch-hint guard,
    /// even though <see cref="Abstractions.StepCollection.FindStep"/> resolves them to the
    /// loop's template child. A key qualifies when any segment after the first parses as a
    /// non-negative integer; the first segment is always the loop step's own (non-numeric) key.
    /// </summary>
    private static bool IsDynamicFanOutKey(string stepKey)
    {
        var segments = stepKey.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length; i++)
        {
            if (int.TryParse(segments[i], out var index) && index >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public async ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, CancellationToken ct = default)
    {
        using var _scope = EngineLogScope.Begin(_logger, runId, flowId, stepKey);

        // Span the retry dispatch separately from the eventual flow.step that runs the
        // re-executed handler. Tags help operators correlate retry storms with run state.
        using var activity = _observabilityOptions.EnableOpenTelemetry
            ? _telemetry.ActivitySource.StartActivity("flow.step.retry", ActivityKind.Internal)
            : null;
        activity?.SetTag("flow.id", flowId.ToString());
        activity?.SetTag("run.id", runId.ToString());
        activity?.SetTag("step.key", stepKey);

        var flow = _flows.Find(flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} not found.");

        var stepMeta = flow.Manifest.Steps.FindStep(stepKey)
            ?? throw new InvalidOperationException($"Step '{stepKey}' not found in flow manifest.");

        // Only a Failed step can be retried (RFC 2.0 S4). This read is a cheap early answer so a retry of a
        // step that is not Failed has no side effect at all (no deadline refresh, no event); it is stale by
        // nature, and the store's conditional reset below is what actually decides.
        var current = await _runStore.GetStepAsync(runId, stepKey).ConfigureAwait(false);
        if (!string.Equals(current?.Status, nameof(StepStatus.Failed), StringComparison.Ordinal))
        {
            activity?.SetTag("flow.step.retry.outcome", nameof(StepRetryOutcome.NotFailed));
            return new StepRetryResult(runId, stepKey, StepRetryOutcome.NotFailed);
        }

        // Cancellation is honoured up to the durable reset and never after it.
        ct.ThrowIfCancellationRequested();

        var ctx = new ExecutionContext { RunId = runId };
        ctx.TriggerData = await _outputsRepository.GetTriggerDataAsync(runId).ConfigureAwait(false);
        ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(runId).ConfigureAwait(false);

        // Give the run a fresh execution window BEFORE anything else observes it as active again.
        // Without this, a step retried after the run's timeout deadline lapsed (or after the run was
        // latched TimedOut) would be immediately skipped by the termination gate in RunStepAsync — the
        // handler would never run. The refreshed deadline reuses trigger-time resolution so it still
        // honours the runaway-loop bound. Refreshing first also closes a window against the periodic
        // timeout sweep: RetryStepAsync below puts the run back into Running, and a sweep landing
        // between that write and this refresh would still see a latched terminal verdict.
        var refreshedDeadline = ResolveTimeoutAtUtc(ctx.TriggerData);
        await _runControlStore.ExtendDeadlineAsync(runId, refreshedDeadline).ConfigureAwait(false);

        // The compare-and-swap: Failed → Running, claim and ledger cleared, in one transaction. Of two
        // concurrent retries that both passed the read above, exactly one gets true here; the other
        // changes nothing. Before 2.0 the reset was unconditional, so the second retry deleted the first
        // one's claim and the handler ran twice at once.
        if (!await _runStore.RetryStepAsync(runId, stepKey).ConfigureAwait(false))
        {
            activity?.SetTag("flow.step.retry.outcome", nameof(StepRetryOutcome.Conflict));
            return new StepRetryResult(runId, stepKey, StepRetryOutcome.Conflict);
        }

        // Durable from here on: nothing below takes the caller's token, so a caller that goes away (an HTTP
        // request whose client disconnected) cannot leave the step reset but never dispatched.
        activity?.SetTag("flow.step.retry.outcome", nameof(StepRetryOutcome.Accepted));
        if (_observabilityOptions.EnableOpenTelemetry)
        {
            _telemetry.StepRetriesCounter.Add(
                1,
                new KeyValuePair<string, object?>("flow_id", flowId.ToString()),
                new KeyValuePair<string, object?>("step_key", MetricStepKey.For(stepKey)));
        }

        // When the original failure of this step caused the DAG continuation to eagerly
        // mark downstream blocked steps as Skipped (with reason
        // StepSkipReasons.PrerequisitesUnmet), those records would prevent the planner
        // from re-evaluating them after the retry succeeds. Clear them transitively so
        // the post-retry continuation can re-dispatch them naturally.
        var cascadeDescendants = ComputeTransitiveDescendants(flow, stepKey);
        if (cascadeDescendants.Count > 0)
        {
            await _runStore.ResetCascadeSkippedDependentsAsync(runId, cascadeDescendants).ConfigureAwait(false);
        }

        await PublishEventSafelyAsync(new StepRetriedEvent
        {
            RunId = runId,
            StepKey = stepKey
        }, CancellationToken.None).ConfigureAwait(false);

        var step = new StepInstance(stepKey, stepMeta.Type)
        {
            RunId = runId,
            TriggerData = ctx.TriggerData,
            TriggerHeaders = ctx.TriggerHeaders,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(stepMeta.Inputs)
        };

        // Dispatched through the runtime like every other attempt, never executed on the caller's thread:
        // before 2.0 the dashboard ran the handler inside the HTTP request while answering "retry enqueued".
        // The reset cleared the ledger row, so the reservation normally succeeds; if another party recorded
        // it in between, dispatch anyway — a duplicate attempt is absorbed by the claim, a missing one would
        // strand the step.
        try
        {
            await DisarmUndeliveredSignalWaiterAsync(runId, stepKey).ConfigureAwait(false);
            await _runStore.TryRecordDispatchAsync(runId, stepKey, CancellationToken.None).ConfigureAwait(false);
            await DispatchReservedStepAsync(ctx, flow, step, delay: null).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Catch-all-and-rethrow: put the step back to Failed so the operator can retry it again, rather
            // than leave it Running with nothing queued. Never swallowed.
            await RestoreFailedAfterUndispatchedRetryAsync(flow, runId, stepKey, current!).ConfigureAwait(false);
            throw;
        }

        return new StepRetryResult(runId, stepKey, StepRetryOutcome.Accepted);
    }

    /// <summary>
    /// Removes the signal waiter of a step being retried when no signal was ever delivered to it, so the
    /// retried <c>WaitForSignal</c> attempt registers a fresh wait instead of re-reading the expired one.
    /// </summary>
    /// <param name="runId">The run owning the retried step.</param>
    /// <param name="stepKey">Runtime key of the retried step.</param>
    /// <remarks>
    /// <para>
    /// <see cref="WaitForSignalHandler"/> leaves an expired waiter in place as a tombstone so a stale
    /// re-invocation fails again instead of re-parking. That is right for a stray delivery, but a manual
    /// retry IS a request to wait again: without this, the retried attempt hit the tombstone on its first
    /// invocation and failed with the same "not received within Ns", so a timed-out wait could never be
    /// retried. Only a step can be retried that is <see cref="StepStatus.Failed"/>, so its wait is over
    /// either way; a waiter that did receive its payload is kept, so a retry of a step that failed after
    /// delivery succeeds with that payload.
    /// </para>
    /// <para>
    /// Runs after the durable reset with <see cref="CancellationToken.None"/>. One point read (and at most
    /// one delete) per manual retry, for every step type: cheaper and more robust than recognising which
    /// handlers park on a signal. A no-op when no <see cref="IFlowSignalStore"/> is registered.
    /// </para>
    /// </remarks>
    private async Task DisarmUndeliveredSignalWaiterAsync(Guid runId, string stepKey)
    {
        if (_signalStore is null)
        {
            return;
        }

        var waiter = await _signalStore.GetWaiterAsync(runId, stepKey, CancellationToken.None).ConfigureAwait(false);
        if (waiter is { DeliveredAt: null })
        {
            await _signalStore.RemoveWaiterAsync(runId, stepKey, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Best-effort undo of an accepted retry whose dispatch threw: moves the step back from
    /// <see cref="StepStatus.Running"/> to <see cref="StepStatus.Failed"/> with its previous output and error,
    /// then closes the run again if nothing else is in flight.
    /// </summary>
    /// <param name="flow">The flow owning the run.</param>
    /// <param name="runId">The run whose retry could not be dispatched.</param>
    /// <param name="stepKey">The retried step.</param>
    /// <param name="previous">The step row as read before the reset.</param>
    /// <remarks>
    /// Runs on the failure path only and swallows its own failure (logged): the caller rethrows the dispatch
    /// exception. The conditional write means a step that a duplicate delivery already started is left alone.
    /// </remarks>
    private async Task RestoreFailedAfterUndispatchedRetryAsync(IFlowDefinition flow, Guid runId, string stepKey, FlowStepRecord previous)
    {
        try
        {
            if (!await _runStore.TryCompleteRunningStepAsync(
                    runId, stepKey, nameof(StepStatus.Failed), previous.OutputJson, previous.ErrorMessage).ConfigureAwait(false))
            {
                return;
            }

            var statuses = await _runtimeStore.GetStepStatusesAsync(runId).ConfigureAwait(false);
            await TryCompleteRunAsync(runId, RunTerminationClassifier.ComputeTerminalStatus(flow, statuses)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.RetryRestoreFailed(_logger, ex, stepKey);
        }
    }

    /// <summary>
    /// Returns the transitive set of top-level step keys whose <c>RunAfter</c> chain
    /// (re)leads back to <paramref name="rootStepKey"/>. Used by <see cref="RetryStepAsync"/>
    /// to identify the dependents whose cascade-skip records must be cleared so the post-retry
    /// continuation can re-evaluate them. Scoped/loop child steps are intentionally
    /// ignored — only the top-level manifest is walked.
    /// </summary>
    private static IReadOnlyCollection<string> ComputeTransitiveDescendants(IFlowDefinition flow, string rootStepKey)
    {
        var descendants = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();
        frontier.Enqueue(rootStepKey);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var kvp in flow.Manifest.Steps)
            {
                var key = kvp.Key;
                if (string.Equals(key, rootStepKey, StringComparison.Ordinal) || descendants.Contains(key))
                {
                    continue;
                }

                var meta = kvp.Value;
                if (meta is null || meta.RunAfter is null || meta.RunAfter.Count == 0)
                {
                    continue;
                }

                if (meta.RunAfter.ContainsKey(current))
                {
                    descendants.Add(key);
                    frontier.Enqueue(key);
                }
            }
        }

        return descendants;
    }

    /// <summary>
    /// Best-effort restore of the dispatch-ledger row for a <see cref="StepStatus.Pending"/>
    /// step whose reschedule threw, ensuring the step is never left simultaneously
    /// claim-released, dispatch-released, and unscheduled.
    /// </summary>
    /// <param name="runId">The run owning the step.</param>
    /// <param name="stepKey">The step whose reschedule failed.</param>
    /// <remarks>
    /// Idempotent: <see cref="IFlowRunStore.TryRecordDispatchAsync"/> is a no-op when the row
    /// already exists (the failure happened inside the dispatcher after the ledger was
    /// re-recorded), and re-inserts it when the failure happened before (the ledger was still
    /// released). Swallows its own exceptions — it runs on the failure path and must never
    /// mask the original reschedule exception, which the caller rethrows.
    /// </remarks>
    private async Task ReassertDispatchAfterFailedRescheduleAsync(Guid runId, string stepKey)
    {
        try
        {
            await _runStore.TryRecordDispatchAsync(runId, stepKey).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.DispatchReassertFailed(_logger, ex, stepKey);
        }
    }

    /// <summary>
    /// Best-effort release of the execution claim after the Pending path's ledger hand-off threw while
    /// the claim was still held, so the runtime's retry of the job can claim the step again.
    /// </summary>
    /// <param name="runId">The run owning the step.</param>
    /// <param name="stepKey">The step whose hand-off failed.</param>
    /// <param name="lease">The attempt's lease; <see langword="null"/> when leases are off.</param>
    /// <remarks>
    /// Swallows its own failure (logged): it runs on the failure path and must never mask the
    /// original exception, which the caller rethrows. If the store is down hard enough that this fails
    /// too, the claim stays held and the run is closed by the timeout sweep — no worse than before.
    /// </remarks>
    private async Task ReleaseClaimAfterFailedHandOffAsync(Guid runId, string stepKey, StepLease? lease)
    {
        try
        {
            await ReleaseClaimAsync(runId, stepKey, lease).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.ClaimReleaseAfterFailedHandOffFailed(_logger, ex, stepKey);
        }
    }

    /// <summary>
    /// Releases this attempt's execution claim: fenced when it holds a lease (a lease another worker took over
    /// is left alone), unconditional otherwise.
    /// </summary>
    /// <param name="runId">The run owning the step.</param>
    /// <param name="stepKey">The claimed step.</param>
    /// <param name="lease">The attempt's lease; <see langword="null"/> when leases are off.</param>
    /// <returns>A task that completes once the release is durable.</returns>
    private Task ReleaseClaimAsync(Guid runId, string stepKey, StepLease? lease)
        => lease is null ? _runtimeStore.ReleaseStepClaimAsync(runId, stepKey) : _runtimeStore.ReleaseStepLeaseAsync(lease);

    /// <summary>
    /// Best-effort release of the execution claim and the dispatch-ledger row of a step whose start
    /// (run-control read or start write) threw after the claim was won, so the step is neither claimed
    /// nor reserved while it has no status row. O(1): two point deletes, on the failure path only.
    /// </summary>
    /// <param name="runId">The run owning the step.</param>
    /// <param name="stepKey">The step whose start failed.</param>
    /// <param name="lease">The attempt's lease; <see langword="null"/> when leases are off.</param>
    /// <remarks>
    /// <para>
    /// The claim goes first. The runtime's retry of the job then claims the step again and runs it; the
    /// released ledger row only means "no attempt is queued", which is true when the runtime gives up, and
    /// lets startup recovery dispatch the ready step in that case. A duplicate dispatch, while the
    /// runtime's own retry is still pending, is absorbed by the claim.
    /// </para>
    /// <para>
    /// Swallows its own failure (logged): it must never mask the original exception, which the caller
    /// rethrows. If the store is down hard enough that the claim release fails too, the claim is orphaned
    /// and <see cref="IFlowRunRuntimeStore.ReleaseOrphanedClaimsAsync"/> releases it once it is older than
    /// <c>FlowRunControlOptions.OrphanedClaimTimeout</c>.
    /// </para>
    /// </remarks>
    private async Task ReleaseAfterFailedStartAsync(Guid runId, string stepKey, StepLease? lease)
    {
        try
        {
            await ReleaseClaimAsync(runId, stepKey, lease).ConfigureAwait(false);
            await _runStore.ReleaseDispatchAsync(runId, stepKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.ReleaseAfterFailedStartFailed(_logger, ex, stepKey);
        }
    }

    /// <summary>
    /// After a <see cref="StepStatus.Pending"/> invocation has released its execution claim, re-reads
    /// the step's signal waiter and enqueues an immediate resume when a payload has already landed.
    /// O(1): one point read per Pending result, plus one dispatch in the rare hit case.
    /// </summary>
    /// <param name="ctx">Execution context of the invocation that just parked.</param>
    /// <param name="flow">Flow definition owning the step.</param>
    /// <param name="step">The step that just parked.</param>
    /// <remarks>
    /// <para>
    /// Closes #190 race 1 by construction. <c>FlowSignalDispatcher</c> wakes a parked step with a
    /// resume nudge, but a delivery that lands while this invocation still holds the claim can only
    /// produce a nudge that loses <see cref="IFlowRunRuntimeStore.TryClaimStepAsync"/> — its short
    /// delay was a guess at how long the claim is held, and on a loaded backend the guess is wrong.
    /// Any delivery that saw the claim held committed <em>before</em> the release, so this read,
    /// issued <em>after</em> the release, observes it: whichever side goes second wakes the step, and
    /// the stranding window is gone rather than narrowed.
    /// </para>
    /// <para>
    /// A duplicate resume (both this and the dispatcher's nudge) is harmless: the claim admits one,
    /// and <c>WaitForSignalHandler</c> returns the same result for a delivered waiter every time.
    /// Best-effort — the reschedule already succeeded, so a storage or dispatch fault here is logged
    /// rather than thrown. An <see cref="OperationCanceledException"/> is the exception: the resume is
    /// dispatched with <see cref="CancellationToken.None"/>, so one means a faulty runtime adapter, and
    /// it propagates exactly as it does from <c>FlowSignalDispatcher</c>.
    /// </para>
    /// <para>
    /// Gated to the built-in <c>WaitForSignal</c> step type: a custom handler that parks while a
    /// delivered waiter exists for its key would be resumed on every attempt — an unbounded hot loop —
    /// whereas the built-in handler completes as soon as it sees a delivered waiter. The gate also
    /// spares every polling step the point read.
    /// </para>
    /// </remarks>
    private async Task ResumeIfSignalLandedWhileClaimedAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        if (_signalStore is null
            || !string.Equals(step.Type, WaitForSignalHandler.StepTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var waiter = await _signalStore.GetWaiterAsync(ctx.RunId, step.Key).ConfigureAwait(false);
            if (waiter is not { DeliveredAt: not null })
            {
                return;
            }

            // A fresh instance rather than the parked one: a runtime that holds on to the scheduled
            // safety-net attempt (the InMemory dispatcher captures it until its delay elapses) must
            // not see its ScheduledTime rewritten underneath it. Inputs come from the manifest
            // template, as FlowSignalDispatcher and RetryStepAsync build theirs: the parked instance
            // carries inputs DefaultStepExecutor already resolved, and resolving those a second time
            // would turn a payload string that merely looks like an expression into a live one.
            var template = flow.Manifest.Steps.FindStep(step.Key);
            var resume = new StepInstance(step.Key, step.Type)
            {
                RunId = ctx.RunId,
                PrincipalId = ctx.PrincipalId,
                TriggerData = ctx.TriggerData,
                TriggerHeaders = ctx.TriggerHeaders,
                ScheduledTime = DateTimeOffset.UtcNow,
                Index = step.Index,
                Inputs = template is not null
                    ? new Dictionary<string, object?>(template.Inputs)
                    : new Dictionary<string, object?>(step.Inputs)
            };

            // CancellationToken.None: the resume must outlive whatever invoked this attempt.
            await _dispatcher.EnqueueStepAsync(ctx, flow, resume, CancellationToken.None).ConfigureAwait(false);
            EngineLog.ResumedSignalDeliveredWhileClaimed(_logger, step.Key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.SignalRecheckFailed(_logger, ex, step.Key);
        }
    }

    /// <summary>
    /// Marks every loop step enclosing <paramref name="step"/> that is still parked on its
    /// completion barrier as <see cref="StepStatus.Skipped"/>, because the run is being terminated
    /// by run control and its iterations will never finish.
    /// </summary>
    /// <param name="ctx">Execution context of the step the termination gate just skipped.</param>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="step">The step the termination gate just skipped.</param>
    /// <param name="terminalStatus">The run-control status forcing termination (Cancelled / TimedOut).</param>
    /// <remarks>
    /// <para>
    /// The termination gate returns before the graph continuation runs, so neither the loop barrier
    /// settle pass nor the blocked-step cascade executes on this path. A parked loop step therefore
    /// stays <see cref="StepStatus.Running"/> forever; <c>HasInFlightWorkAsync</c> keeps reporting
    /// in-flight work, and the run can be closed by neither <c>TryCompleteRunAsync</c> nor the
    /// periodic timeout sweep — only by a host restart, via <c>FlowRunRecoveryHostedService</c>.
    /// </para>
    /// <para>
    /// Settling the barrier instead of abandoning it is not sufficient: a loop body with more than
    /// one step (the shape in issue #169 — <c>wait</c> then <c>consume</c>) only ever fans out its
    /// entry child, so the dependent children never get a status row on this path and "all
    /// iterations terminal" can never become true. <see cref="StepStatus.Skipped"/> also matches
    /// what the gate records for the step itself, and leaves steps downstream of the loop with no
    /// row at all — the same shape a cancelled linear flow already produces.
    /// </para>
    /// </remarks>
    private async Task AbandonEnclosingLoopsAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance step,
        string terminalStatus)
    {
        var enclosing = LoopBarrier.EnclosingLoopKeys(step.Key);
        if (enclosing.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, StepStatus> statuses;
        try
        {
            // Keyed: only the enclosing loops' rows matter. Every step a cancelled run still drains takes this
            // path, so reading the whole run here was O(steps in the run) per drained step (#189 P0-1).
            statuses = await _runtimeStore.GetStepStatusesAsync(ctx.RunId, enclosing).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.StepSkipTrackingFailed(_logger, ex, step.Key);
            return;
        }

        foreach (var loopKey in enclosing)
        {
            if (!statuses.TryGetValue(loopKey, out var loopStatus)
                || loopStatus != StepStatus.Running
                || flow.Manifest.Steps.FindStep(loopKey) is not IScopedStep)
            {
                continue;
            }

            try
            {
                // Conditional on Running, like the barrier settle: a loop a concurrent continuation
                // settled a moment ago keeps its Succeeded row instead of being overwritten.
                await _runStore.TryCompleteRunningStepAsync(
                    ctx.RunId,
                    loopKey,
                    StepStatus.Skipped.ToString(),
                    null,
                    $"Run is {terminalStatus}.").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EngineLog.StepSkipTrackingFailed(_logger, ex, loopKey);
            }
        }
    }

    /// <summary>
    /// Delay before a step handed back by a worker whose manifest does not contain it is redelivered.
    /// </summary>
    /// <remarks>
    /// Long enough that a rolling deploy is not flooded by redeliveries bouncing between stale replicas,
    /// short enough that the step is not noticeably late once a current replica picks it up.
    /// </remarks>
    internal static readonly TimeSpan UnknownStepRedeliveryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Handles a step whose key the executing worker's manifest does not contain: hands it back to the
    /// runtime when another manifest version is registered for the flow, so a worker that knows the step
    /// runs it; otherwise lets the caller record it as removed.
    /// </summary>
    /// <param name="ctx">Execution context of the run.</param>
    /// <param name="flow">The flow definition this worker holds.</param>
    /// <param name="step">The step this worker was handed.</param>
    /// <returns>
    /// <see langword="true"/> when the step was redelivered and the caller must exit without claiming it;
    /// <see langword="false"/> when this worker holds the registered manifest (the step was removed from the
    /// flow) or no manifest is registered, and the step is recorded <see cref="StepStatus.Skipped"/> by the
    /// ordinary path.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The worker cannot tell from its own manifest whether the step was added in a version it has not
    /// loaded yet, or removed in the version it has. The flow store can: every host syncs its manifest there
    /// at startup (<c>FlowSyncHostedService</c>), so a stored manifest that differs from this worker's means
    /// another version is live. Only a worker that holds the registered manifest takes the terminal decision.
    /// When the deploy converges — every replica restarted, or rolled back — the stored manifest matches
    /// every worker again and the step is decided on its next delivery, so a hand-back cannot loop for ever.
    /// </para>
    /// <para>
    /// The dispatch-ledger row this delivery was queued under is kept, so the run never reads as finished
    /// while the step is in transit; the redelivery reuses it. A store read that fails hands the step back
    /// too: a Skipped row cannot be undone, a redelivery can be repeated.
    /// </para>
    /// </remarks>
    private async Task<bool> TryHandBackStepUnknownToManifestAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        bool anotherVersionRegistered;
        try
        {
            var record = await _flowStore.GetByIdAsync(flow.Id).ConfigureAwait(false);
            anotherVersionRegistered = record?.ManifestJson is { } stored && !RegisteredManifest.Matches(stored, flow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.UnknownStepManifestCheckFailed(_logger, ex, step.Key);
            anotherVersionRegistered = true;
        }

        if (!anotherVersionRegistered)
        {
            EngineLog.UnknownStepRemovedFromManifest(_logger, step.Key, flow.Id);
            return false;
        }

        EngineLog.UnknownStepHandedBack(_logger, step.Key, flow.Id, UnknownStepRedeliveryDelay.TotalSeconds);

        // The ledger row is held; DispatchReservedStepAsync releases it if the runtime rejects the message,
        // and the throw makes the runtime retry this delivery.
        await DispatchReservedStepAsync(ctx, flow, step, UnknownStepRedeliveryDelay).ConfigureAwait(false);
        return true;
    }

    private async Task RecordSkippedCurrentStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, string terminalStatus)
    {
        try
        {
            await _runStore.RecordStepStartAsync(ctx.RunId, step.Key, step.Type, null, null).ConfigureAwait(false);
            await _runStore.RecordStepCompleteAsync(
                ctx.RunId,
                step.Key,
                StepStatus.Skipped.ToString(),
                null,
                $"Run is {terminalStatus}.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.StepSkipTrackingFailed(_logger, ex, step.Key);
        }
    }
}
