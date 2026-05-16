using System.Diagnostics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Per-step execution partial of <see cref="FlowOrchestratorEngine"/>:
/// <see cref="RunStepAsync"/>, <see cref="RetryStepAsync"/>, claim guards, control-store
/// termination check, handler dispatch, and dispatch-hint fan-out.
/// </summary>
public sealed partial class FlowOrchestratorEngine
{
    /// <inheritdoc/>
    public async ValueTask<object?> RunStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, CancellationToken ct = default)
    {
        using var activity = _observabilityOptions.EnableOpenTelemetry
            ? _telemetry.ActivitySource.StartActivity("flow.step", ActivityKind.Internal)
            : null;

        await EnsureTriggerDataAsync(ctx).ConfigureAwait(false);
        _contextAccessor.CurrentContext = ctx;

        using var _scope = EngineLogScope.Begin(_logger, ctx.RunId, flow.Id, step.Key);

        activity?.SetTag("flow.id", flow.Id.ToString());
        activity?.SetTag("run.id", ctx.RunId.ToString());
        activity?.SetTag("step.key", step.Key);
        activity?.SetTag("step.type", step.Type);

        var queueDelayMs = Math.Max(0d, (DateTimeOffset.UtcNow - step.ScheduledTime).TotalMilliseconds);
        if (_observabilityOptions.EnableOpenTelemetry)
        {
            _telemetry.QueueDelayMs.Record(
                queueDelayMs,
                new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                new KeyValuePair<string, object?>("step_key", step.Key));
        }

        try
        {
            // Execute-time claim guard (v1.22+). Atomic INSERT into FlowStepClaims; only the first
            // worker to call this for (runId, stepKey) wins, the rest exit silently. Critical for
            // at-least-once delivery models where one enqueued message can reach multiple
            // consumers (Service Bus topic-broadcast, redelivery after a worker timeout, etc.).
            // Pre-1.22 the claim was at schedule time which assumed 1:1 enqueue→execute — broken
            // under broadcast. Released by RetryStepAsync and the Pending re-schedule path so the
            // SAME step can claim again on a fresh attempt.
            if (_runtimeStore is not null)
            {
                var claimed = await _runtimeStore.TryClaimStepAsync(ctx.RunId, step.Key).ConfigureAwait(false);
                if (!claimed)
                {
                    activity?.SetTag("flow.step.claim_lost", true);
                    return null;
                }
            }

            var controlStatus = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);
            if (controlStatus is not null)
            {
                await RecordSkippedCurrentStepAsync(ctx, flow, step, controlStatus).ConfigureAwait(false);
                await TryCompleteRunAsync(ctx.RunId, controlStatus).ConfigureAwait(false);
                return null;
            }

            string? inputJson = null;
            try
            {
                await _outputsRepository.SaveStepInputAsync(ctx, flow, step).ConfigureAwait(false);
                inputJson = SafeSerialize(step.Inputs);
                await _runStore.RecordStepStartAsync(ctx.RunId, step.Key, step.Type, inputJson, ctx.JobId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EngineLog.StepStartTrackingFailed(_logger, ex);
            }

            await RecordEventAsync(ctx, flow, step, "step.started", $"Step '{step.Key}' started.").ConfigureAwait(false);

            var stepExecutionStart = Stopwatch.GetTimestamp();
            IStepResult result;
            Exception? handlerException = null;
            try
            {
                result = await _stepExecutor.ExecuteAsync(ctx, flow, step).ConfigureAwait(false);
                await _outputsRepository.SaveStepOutputAsync(ctx, flow, step, result).ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions] handler boundary: any exception (incl. OCE) becomes a Failed StepResult by design
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

            try
            {
                var outputJson = SafeSerialize(result.Result);
                await _runStore.RecordStepCompleteAsync(ctx.RunId, step.Key, result.Status.ToString(), outputJson, result.FailedReason)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EngineLog.StepCompletionTrackingFailed(_logger, ex);
            }

            var stepExecutionMs = Stopwatch.GetElapsedTime(stepExecutionStart).TotalMilliseconds;
            if (_observabilityOptions.EnableOpenTelemetry)
            {
                _telemetry.StepDurationMs.Record(
                    stepExecutionMs,
                    new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                    new KeyValuePair<string, object?>("step_key", step.Key),
                    new KeyValuePair<string, object?>("status", result.Status.ToString()));

                _telemetry.StepCompletedCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("flow_id", flow.Id.ToString()),
                    new KeyValuePair<string, object?>("status", result.Status.ToString()));
            }

            await RecordEventAsync(
                ctx,
                flow,
                step,
                result.Status == StepStatus.Failed ? "step.failed" : "step.completed",
                result.Status == StepStatus.Failed
                    ? result.FailedReason ?? $"Step '{step.Key}' failed."
                    : $"Step '{step.Key}' completed with status {result.Status}.").ConfigureAwait(false);

            // Pending is non-terminal — only publish step.completed for terminal statuses.
            if (result.Status != StepStatus.Pending)
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
                        new KeyValuePair<string, object?>("step_key", step.Key));
                }

                var controlAfterPending = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);
                if (controlAfterPending is not null)
                {
                    await TryCompleteRunAsync(ctx.RunId, controlAfterPending).ConfigureAwait(false);
                    return result.Result;
                }

                var retryDelay = result.DelayNextStep ?? TimeSpan.FromSeconds(10);
                // Release BOTH guards so the next poll attempt can claim + dispatch:
                //   - Dispatch ledger: ReleaseDispatchAsync clears the FlowStepDispatches row
                //   - Execute claim:   ReleaseStepClaimAsync clears the FlowStepClaims row (v1.22+)
                // Pre-1.22 only the dispatch was released; the claim leaked across attempts and
                // Pending poll silently no-op'd (the v2-runtime-claim-leak known issue).
                await _runStore.ReleaseDispatchAsync(ctx.RunId, step.Key).ConfigureAwait(false);
                if (_runtimeStore is not null)
                {
                    await _runtimeStore.ReleaseStepClaimAsync(ctx.RunId, step.Key).ConfigureAwait(false);
                }
                step.ScheduledTime = DateTimeOffset.UtcNow + retryDelay;
                await TryScheduleStepAsync(ctx, flow, step, retryDelay).ConfigureAwait(false);

                await RecordEventAsync(ctx, flow, step, "step.pending", $"Step '{step.Key}' pending for {retryDelay}.")
                    .ConfigureAwait(false);
                return result.Result;
            }

            // Dispatch dynamic child steps declared by the handler (e.g. ForEach iterations).
            // Validation: hints must NOT target static DAG steps — only dynamic fan-out is allowed.
            if (result.DispatchHint?.Spawn is { Count: > 0 } hintChildren)
            {
                foreach (var child in hintChildren)
                {
                    if (flow.Manifest.Steps.FindStep(child.StepKey) is not null)
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

            if (_runtimeStore is null)
            {
                await RunLegacySequentialContinuationAsync(ctx, flow, step, result).ConfigureAwait(false);
            }
            else
            {
                await RunGraphContinuationAsync(ctx, flow, step, result).ConfigureAwait(false);
            }

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

        if (_observabilityOptions.EnableOpenTelemetry)
        {
            _telemetry.StepRetriesCounter.Add(
                1,
                new KeyValuePair<string, object?>("flow_id", flowId.ToString()),
                new KeyValuePair<string, object?>("step_key", stepKey));
        }

        var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flow = flows.FindById(flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} not found.");

        var stepMeta = flow.Manifest.Steps.FindStep(stepKey)
            ?? throw new InvalidOperationException($"Step '{stepKey}' not found in flow manifest.");

        await _runStore.RetryStepAsync(runId, stepKey).ConfigureAwait(false);

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
        }, ct).ConfigureAwait(false);

        var step = new StepInstance(stepKey, stepMeta.Type)
        {
            RunId = runId,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(stepMeta.Inputs)
        };

        var ctx = new ExecutionContext { RunId = runId };
        ctx.TriggerData = await _outputsRepository.GetTriggerDataAsync(runId).ConfigureAwait(false);
        ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(runId).ConfigureAwait(false);

        return await RunStepAsync(ctx, flow, step, ct).ConfigureAwait(false);
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
