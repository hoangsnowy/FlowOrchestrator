using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Runtime-neutral implementation of <see cref="IFlowOrchestrator"/>.
/// Contains the full flow execution lifecycle — trigger, step execution, graph continuation,
/// polling, retry, and cancellation — with zero runtime-specific dependencies.
/// Hangfire, in-memory channels, and queue consumers all delegate to this engine.
/// </summary>
/// <remarks>
/// Supports two continuation modes:
/// <list type="bullet">
///   <item><b>Graph mode</b> (default when <see cref="IFlowRunRuntimeStore"/> is registered):
///     evaluates the full DAG after each step, enabling parallel fan-out, loop steps, and skip propagation.</item>
///   <item><b>Legacy sequential mode</b>: simple linear next-step resolution when no runtime store is available.</item>
/// </list>
/// </remarks>
public sealed class FlowOrchestratorEngine : IFlowOrchestrator
{
    private readonly IStepDispatcher _dispatcher;
    private readonly IFlowExecutor _flowExecutor;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly IStepExecutor _stepExecutor;
    private readonly IFlowStore _flowStore;
    private readonly IFlowRunStore _runStore;
    private readonly IOutputsRepository _outputsRepository;
    private readonly IExecutionContextAccessor _contextAccessor;
    private readonly IFlowRepository _flowRepository;
    private readonly IFlowRunRuntimeStore? _runtimeStore;
    private readonly IFlowRunControlStore? _runControlStore;
    private readonly FlowRunControlOptions _runControlOptions;
    private readonly FlowObservabilityOptions _observabilityOptions;
    private readonly FlowOrchestratorTelemetry _telemetry;
    private readonly ILogger<FlowOrchestratorEngine> _logger;
    private readonly WhenClauseEvaluator _whenEvaluator;

    /// <summary>Initialises the engine with all required and optional dependencies.</summary>
    public FlowOrchestratorEngine(
        IStepDispatcher dispatcher,
        IFlowExecutor flowExecutor,
        IFlowGraphPlanner graphPlanner,
        IStepExecutor stepExecutor,
        IFlowStore flowStore,
        IFlowRunStore runStore,
        IOutputsRepository outputsRepository,
        IExecutionContextAccessor contextAccessor,
        IFlowRepository flowRepository,
        IEnumerable<IFlowRunRuntimeStore> runtimeStores,
        IEnumerable<IFlowRunControlStore> runControlStores,
        FlowRunControlOptions runControlOptions,
        FlowObservabilityOptions observabilityOptions,
        FlowOrchestratorTelemetry telemetry,
        ILogger<FlowOrchestratorEngine> logger)
    {
        _dispatcher = dispatcher;
        _flowExecutor = flowExecutor;
        _graphPlanner = graphPlanner;
        _stepExecutor = stepExecutor;
        _flowStore = flowStore;
        _runStore = runStore;
        _outputsRepository = outputsRepository;
        _contextAccessor = contextAccessor;
        _flowRepository = flowRepository;
        _runtimeStore = runtimeStores.FirstOrDefault();
        _runControlStore = runControlStores.FirstOrDefault();
        _runControlOptions = runControlOptions;
        _observabilityOptions = observabilityOptions;
        _telemetry = telemetry;
        _logger = logger;
        _whenEvaluator = new WhenClauseEvaluator(outputsRepository, runStore);

        if (_runtimeStore is null)
        {
            EngineLog.LegacySequentialMode(_logger);
        }
    }

    private static string? SafeSerialize(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Undefined ? null : element.GetRawText();
        return JsonSerializer.Serialize(value);
    }

    private static bool IsInFlight(StepStatus status) =>
        status is StepStatus.Running or StepStatus.Pending;

    private async ValueTask EnsureTriggerDataAsync(IExecutionContext ctx)
    {
        var fromRepo = await _outputsRepository.GetTriggerDataAsync(ctx.RunId).ConfigureAwait(false);
        if (fromRepo is not null)
        {
            ctx.TriggerData = fromRepo;
        }
        else if (ctx.TriggerData is null)
        {
            if (ctx is ITriggerContext triggerContext && triggerContext.Trigger is not null)
            {
                ctx.TriggerData = triggerContext.Trigger.Data;
            }
        }

        if (ctx.TriggerHeaders is null)
        {
            ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(ctx.RunId).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<object?> TriggerAsync(ITriggerContext triggerContext, CancellationToken ct = default)
    {
        using var activity = _observabilityOptions.EnableOpenTelemetry
            ? _telemetry.ActivitySource.StartActivity("flow.trigger", ActivityKind.Internal)
            : null;

        triggerContext.RunId = triggerContext.RunId == Guid.Empty ? Guid.NewGuid() : triggerContext.RunId;
        triggerContext.TriggerData = triggerContext.Trigger.Data;
        triggerContext.TriggerHeaders = triggerContext.Trigger.Headers;
        _contextAccessor.CurrentContext = triggerContext;

        // BeginScope ensures every nested log line in this run carries RunId/FlowId,
        // including logs emitted by user-written step handlers further down the stack.
        using var _scope = EngineLogScope.Begin(_logger, triggerContext.RunId, triggerContext.Flow.Id);

        activity?.SetTag("flow.id", triggerContext.Flow.Id.ToString());
        activity?.SetTag("run.id", triggerContext.RunId.ToString());
        activity?.SetTag("trigger.key", triggerContext.Trigger.Key);
        activity?.SetTag("trigger.type", triggerContext.Trigger.Type);

        try
        {
            // Reject the trigger early if the flow has been disabled via the dashboard / API.
            // Silent skip (no exception) so a webhook caller doesn't see a 5xx, and so cron
            // ticks racing the disable propagation don't spam alerts.
            var record = await _flowStore.GetByIdAsync(triggerContext.Flow.Id).ConfigureAwait(false);
            if (record is { IsEnabled: false })
            {
                EngineLog.TriggerRejectedDisabledFlow(_logger, triggerContext.Flow.Id, triggerContext.Trigger.Key);
                activity?.SetTag("flow.disabled", true);
                return new { runId = (Guid?)null, disabled = true };
            }

            var idempotencyKey = TryGetIdempotencyKey(triggerContext.TriggerHeaders);
            if (_runControlStore is not null && !string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existingRunId = await _runControlStore
                    .FindRunIdByIdempotencyKeyAsync(triggerContext.Flow.Id, triggerContext.Trigger.Key, idempotencyKey)
                    .ConfigureAwait(false);
                if (existingRunId.HasValue)
                {
                    triggerContext.RunId = existingRunId.Value;
                    activity?.SetTag("duplicate", true);
                    return new { runId = existingRunId.Value, duplicate = true };
                }

                var registered = await _runControlStore
                    .TryRegisterIdempotencyKeyAsync(triggerContext.Flow.Id, triggerContext.Trigger.Key, idempotencyKey, triggerContext.RunId)
                    .ConfigureAwait(false);
                if (!registered)
                {
                    existingRunId = await _runControlStore
                        .FindRunIdByIdempotencyKeyAsync(triggerContext.Flow.Id, triggerContext.Trigger.Key, idempotencyKey)
                        .ConfigureAwait(false);

                    if (existingRunId.HasValue)
                    {
                        triggerContext.RunId = existingRunId.Value;
                        activity?.SetTag("duplicate", true);
                        return new { runId = existingRunId.Value, duplicate = true };
                    }
                }
            }

            var triggerDataJson = SafeSerialize(triggerContext.Trigger.Data);

            try
            {
                await _runStore.StartRunAsync(
                    triggerContext.Flow.Id,
                    triggerContext.Flow.GetType().Name,
                    triggerContext.RunId,
                    triggerContext.Trigger.Key,
                    triggerDataJson,
                    triggerContext.JobId,
                    triggerContext.SourceRunId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EngineLog.RunStartTrackingFailed(_logger, ex);
            }

            if (_runControlStore is not null)
            {
                var timeoutAt = ResolveTimeoutAtUtc(triggerContext.TriggerData);
                await _runControlStore.ConfigureRunAsync(
                    triggerContext.RunId,
                    triggerContext.Flow.Id,
                    triggerContext.Trigger.Key,
                    idempotencyKey,
                    timeoutAt).ConfigureAwait(false);
            }

            await _outputsRepository.SaveTriggerDataAsync(triggerContext, triggerContext.Flow, triggerContext.Trigger).ConfigureAwait(false);
            await _outputsRepository.SaveTriggerHeadersAsync(triggerContext, triggerContext.Flow, triggerContext.Trigger).ConfigureAwait(false);

            var entries = _graphPlanner.CreateEntrySteps(triggerContext);
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("No entry step found for flow.");
            }

            var anyEntryScheduled = false;
            var anyEntrySkipped = false;
            foreach (var entry in entries)
            {
                var skipped = await TryEvaluateWhenAndSkipAsync(triggerContext, triggerContext.Flow, entry.Key).ConfigureAwait(false);
                if (skipped)
                {
                    anyEntrySkipped = true;
                    continue;
                }

                if (await TryScheduleStepAsync(triggerContext, triggerContext.Flow, entry, null).ConfigureAwait(false))
                {
                    anyEntryScheduled = true;
                }
            }

            // If every entry step was skipped by a When clause, kick off a single graph
            // continuation pass so dependents can cascade or the run can terminate cleanly.
            if (!anyEntryScheduled && anyEntrySkipped && _runtimeStore is not null)
            {
                await RunGraphContinuationAsync(
                    triggerContext,
                    triggerContext.Flow,
                    CreateRunEventStep(triggerContext.RunId),
                    new StepResult { Key = "__entry_skipped", Status = StepStatus.Skipped }).ConfigureAwait(false);
            }

            if (_observabilityOptions.EnableOpenTelemetry)
            {
                _telemetry.RunStartedCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("flow_id", triggerContext.Flow.Id.ToString()),
                    new KeyValuePair<string, object?>("trigger_key", triggerContext.Trigger.Key));
            }

            await RecordEventAsync(
                triggerContext,
                triggerContext.Flow,
                CreateRunEventStep(triggerContext.RunId),
                "run.started",
                $"Flow run started via trigger '{triggerContext.Trigger.Key}'.").ConfigureAwait(false);

            return new { runId = triggerContext.RunId, duplicate = false };
        }
        catch (Exception ex)
        {
            // Mark the trigger activity as failed before propagating. Without this, APM tools
            // see a span that simply ended with no error indicator and never raise an alert.
            activity.RecordError(ex);
            throw;
        }
        finally
        {
            _contextAccessor.CurrentContext = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<object?> TriggerByScheduleAsync(Guid flowId, string triggerKey, string? jobId = null, CancellationToken ct = default)
    {
        var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flow = flows.FindById(flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} not found.");

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger(triggerKey, "Cron", null),
            JobId = jobId
        };

        return await TriggerAsync(ctx, ct).ConfigureAwait(false);
    }

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
            catch (Exception ex)
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
                    activity.RecordError(handlerException);
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
            catch (Exception ex)
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
            activity.RecordError(ex);
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
                "Prerequisite status did not satisfy runAfter conditions.").ConfigureAwait(false);

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
        evaluation = _graphPlanner.Evaluate(flow, statuses);

        var termination = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);
        var enqueued = 0;
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

        if (termination is null)
        {
            // Single-pass status tally — three previous LINQ Any passes over
            // statuses.Values are coalesced into one foreach with three booleans.
            // For a 50-step flow this drops 150 enumerator allocations to 1.
            var anySucceeded = false;
            var anyFailed = false;
            var anySkipped = false;
            foreach (var s in statuses.Values)
            {
                switch (s)
                {
                    case StepStatus.Succeeded: anySucceeded = true; break;
                    case StepStatus.Failed:    anyFailed    = true; break;
                    case StepStatus.Skipped:   anySkipped   = true; break;
                }
            }

            if (!anySucceeded)
            {
                termination = anyFailed
                    ? StepStatus.Failed.ToString()
                    : anySkipped
                        ? StepStatus.Skipped.ToString()
                        : StepStatus.Failed.ToString();
            }
            else
            {
                var hasUnhandledFailure = false;
                if (anyFailed)
                {
                    foreach (var kvp in statuses)
                    {
                        if (kvp.Value == StepStatus.Failed
                            && !IsFailureHandled(kvp.Key, flow.Manifest.Steps, statuses))
                        {
                            hasUnhandledFailure = true;
                            break;
                        }
                    }
                }

                if (hasUnhandledFailure)
                {
                    termination = StepStatus.Failed.ToString();
                }
                else
                {
                    // Determine "all leaves skipped" without materialising a HashSet.
                    // A leaf is a step whose key never appears in any RunAfter map.
                    // We want: leafKeys.Count > 0 && every leaf has Skipped status.
                    var leafCount = 0;
                    var allLeavesSkipped = true;
                    foreach (var key in statuses.Keys)
                    {
                        if (IsLeaf(key, flow.Manifest.Steps))
                        {
                            leafCount++;
                            if (!statuses.TryGetValue(key, out var status) || status != StepStatus.Skipped)
                            {
                                allLeavesSkipped = false;
                                // Can't break — leafCount must reach > 0 for the
                                // outer condition. But once we know !skipped, we
                                // still need leafCount; cheaper to count out.
                            }
                        }
                    }

                    termination = (leafCount > 0 && allLeavesSkipped)
                        ? StepStatus.Skipped.ToString()
                        : StepStatus.Succeeded.ToString();
                }
            }
        }

        await TryCompleteRunAsync(ctx.RunId, termination).ConfigureAwait(false);
        await RecordEventAsync(
            ctx,
            flow,
            step,
            "run.completed",
            $"Run completed with status {termination}.").ConfigureAwait(false);
    }

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
            catch (Exception ex)
            {
                EngineLog.DispatchAnnotateFailed(_logger, ex, step.Key);
            }
        }

        return true;
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
            Activity.Current.RecordError(ex);
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
        catch (Exception ex)
        {
            EngineLog.StepSkipTrackingFailed(_logger, ex, step.Key);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when a failed step has at least one downstream step
    /// that ran and <see cref="StepStatus.Succeeded"/>. This indicates the flow author
    /// explicitly designed a recovery path that executed successfully.
    /// </summary>
    private static bool IsFailureHandled(
        string failedStepKey,
        StepCollection manifestSteps,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        // Imperative form to avoid the LINQ Any enumerator allocation on a path
        // hit once per failed step per termination-check. Identical semantics:
        // returns true if any manifest step that runs after the failure ran and
        // succeeded, indicating an explicit recovery handler.
        foreach (var kvp in manifestSteps)
        {
            if (kvp.Value.RunAfter?.ContainsKey(failedStepKey) == true
                && statuses.TryGetValue(kvp.Key, out var s)
                && s == StepStatus.Succeeded)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when no manifest step references
    /// <paramref name="key"/> in its <c>RunAfter</c> map — i.e., the step is a
    /// leaf of the DAG. Single-pass, allocation-free; used by the termination
    /// classifier instead of a per-call HashSet build.
    /// </summary>
    private static bool IsLeaf(string key, StepCollection manifestSteps)
    {
        foreach (var kvp in manifestSteps)
        {
            if (kvp.Value.RunAfter?.ContainsKey(key) == true)
            {
                return false;
            }
        }
        return true;
    }

    private async Task TryCompleteRunAsync(Guid runId, string status)
    {
        if (_runtimeStore is not null)
        {
            var statuses = await _runtimeStore.GetStepStatusesAsync(runId).ConfigureAwait(false);
            if (statuses.Values.Any(IsInFlight))
            {
                return;
            }

            var claimed = await _runtimeStore.GetClaimedStepKeysAsync(runId).ConfigureAwait(false);
            if (claimed.Except(statuses.Keys, StringComparer.Ordinal).Any())
            {
                return;
            }
        }

        await _runStore.CompleteRunAsync(runId, status).ConfigureAwait(false);

        if (_observabilityOptions.EnableOpenTelemetry)
        {
            _telemetry.RunCompletedCounter.Add(
                1,
                new KeyValuePair<string, object?>("status", status));
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

        if (control.TimeoutAtUtc is not null && DateTimeOffset.UtcNow >= control.TimeoutAtUtc.Value)
        {
            await _runControlStore.MarkTimedOutAsync(runId, "Run timed out before scheduling next step.").ConfigureAwait(false);
            return "TimedOut";
        }

        if (control.CancelRequested)
        {
            return "Cancelled";
        }

        return null;
    }

    private TimeSpan? ResolveTimeoutFromTriggerData(object? triggerData)
    {
        if (triggerData is null)
        {
            return null;
        }

        if (triggerData is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("runTimeoutSeconds", out var timeoutProp)
                && timeoutProp.TryGetInt32(out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return null;
        }

        if (triggerData is IDictionary<string, object?> map
            && map.TryGetValue("runTimeoutSeconds", out var value))
        {
            if (value is int i && i > 0) return TimeSpan.FromSeconds(i);
            if (value is long l && l > 0) return TimeSpan.FromSeconds(l);
            if (value is string s && int.TryParse(s, out var parsed) && parsed > 0) return TimeSpan.FromSeconds(parsed);
        }

        return null;
    }

    private DateTimeOffset? ResolveTimeoutAtUtc(object? triggerData)
    {
        var timeout = ResolveTimeoutFromTriggerData(triggerData) ?? _runControlOptions.DefaultRunTimeout;
        if (timeout is null || timeout <= TimeSpan.Zero)
        {
            return null;
        }

        return DateTimeOffset.UtcNow + timeout.Value;
    }

    private string? TryGetIdempotencyKey(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || string.IsNullOrWhiteSpace(_runControlOptions.IdempotencyHeaderName))
        {
            return null;
        }

        if (!headers.TryGetValue(_runControlOptions.IdempotencyHeaderName, out var value))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

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
        catch (Exception ex)
        {
            EngineLog.EventPersistenceFailed(_logger, ex, type);
        }
    }

    private static IStepInstance CreateRunEventStep(Guid runId)
    {
        return new StepInstance("__run", "Run")
        {
            RunId = runId,
            ScheduledTime = DateTimeOffset.UtcNow
        };
    }
}
