using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Central orchestrator that implements both <see cref="IHangfireFlowTrigger"/> and
/// <see cref="IHangfireStepRunner"/>, driving the full flow execution lifecycle via Hangfire.
/// </summary>
/// <remarks>
/// Supports two continuation modes depending on whether <c>IFlowRunRuntimeStore</c> is registered:
/// <list type="bullet">
///   <item><b>Graph mode</b> (default): evaluates the full DAG after each step, supporting parallel fan-out, loop steps, and skip propagation.</item>
///   <item><b>Legacy sequential mode</b>: falls back to simple linear next-step resolution when no runtime store is available.</item>
/// </list>
/// </remarks>
public sealed class HangfireFlowOrchestrator : IHangfireFlowTrigger, IHangfireStepRunner
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IFlowExecutor _flowExecutor;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly IStepExecutor _stepExecutor;
    private readonly IFlowRunStore _runStore;
    private readonly IOutputsRepository _outputsRepository;
    private readonly IExecutionContextAccessor _contextAccessor;
    private readonly IFlowRepository _flowRepository;
    private readonly IFlowRunRuntimeStore? _runtimeStore;
    private readonly IFlowRunControlStore? _runControlStore;
    private readonly FlowRunControlOptions _runControlOptions;
    private readonly FlowObservabilityOptions _observabilityOptions;
    private readonly FlowOrchestratorTelemetry _telemetry;
    private readonly ILogger<HangfireFlowOrchestrator> _logger;

    /// <summary>Initialises the orchestrator with all required and optional dependencies.</summary>
    public HangfireFlowOrchestrator(
        IBackgroundJobClient backgroundJobClient,
        IFlowExecutor flowExecutor,
        IFlowGraphPlanner graphPlanner,
        IStepExecutor stepExecutor,
        IFlowRunStore runStore,
        IOutputsRepository outputsRepository,
        IExecutionContextAccessor contextAccessor,
        IFlowRepository flowRepository,
        IEnumerable<IFlowRunRuntimeStore> runtimeStores,
        IEnumerable<IFlowRunControlStore> runControlStores,
        FlowRunControlOptions runControlOptions,
        FlowObservabilityOptions observabilityOptions,
        FlowOrchestratorTelemetry telemetry,
        ILogger<HangfireFlowOrchestrator> logger)
    {
        _backgroundJobClient = backgroundJobClient;
        _flowExecutor = flowExecutor;
        _graphPlanner = graphPlanner;
        _stepExecutor = stepExecutor;
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

        if (_runtimeStore is null)
        {
            _logger.LogDebug(
                "No IFlowRunRuntimeStore registered. Running in legacy sequential mode — parallel graph evaluation and step-claim deduplication are disabled.");
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

    public async ValueTask<object?> TriggerAsync(ITriggerContext triggerContext, PerformContext? performContext = null)
    {
        using var activity = _observabilityOptions.EnableOpenTelemetry
            ? _telemetry.ActivitySource.StartActivity("flow.trigger", ActivityKind.Internal)
            : null;

        triggerContext.RunId = triggerContext.RunId == Guid.Empty ? Guid.NewGuid() : triggerContext.RunId;
        triggerContext.TriggerData = triggerContext.Trigger.Data;
        triggerContext.TriggerHeaders = triggerContext.Trigger.Headers;
        _contextAccessor.CurrentContext = triggerContext;

        activity?.SetTag("flow.id", triggerContext.Flow.Id.ToString());
        activity?.SetTag("run.id", triggerContext.RunId.ToString());
        activity?.SetTag("trigger.key", triggerContext.Trigger.Key);
        activity?.SetTag("trigger.type", triggerContext.Trigger.Type);

        try
        {
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
                    performContext?.BackgroundJob?.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track flow run start.");
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

            foreach (var entry in entries)
            {
                await TryScheduleStepAsync(triggerContext, triggerContext.Flow, entry, null).ConfigureAwait(false);
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
        finally
        {
            _contextAccessor.CurrentContext = null;
        }
    }

    public async ValueTask<object?> RunStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, PerformContext? performContext = null)
    {
        using var activity = _observabilityOptions.EnableOpenTelemetry
            ? _telemetry.ActivitySource.StartActivity("flow.step", ActivityKind.Internal)
            : null;

        await EnsureTriggerDataAsync(ctx).ConfigureAwait(false);
        _contextAccessor.CurrentContext = ctx;

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
                await _runStore.RecordStepStartAsync(ctx.RunId, step.Key, step.Type, inputJson, performContext?.BackgroundJob?.Id)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track step start.");
            }

            await RecordEventAsync(ctx, flow, step, "step.started", $"Step '{step.Key}' started.").ConfigureAwait(false);

            var stepExecutionStart = Stopwatch.GetTimestamp();
            IStepResult result;
            try
            {
                result = await _stepExecutor.ExecuteAsync(ctx, flow, step).ConfigureAwait(false);
                await _outputsRepository.SaveStepOutputAsync(ctx, flow, step, result).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Step execution failed for {StepKey}", step.Key);
                result = new StepResult
                {
                    Key = step.Key,
                    Status = StepStatus.Failed,
                    FailedReason = ex.ToString(),
                    ReThrow = false
                };
            }

            try
            {
                var outputJson = SafeSerialize(result.Result);
                await _runStore.RecordStepCompleteAsync(ctx.RunId, step.Key, result.Status.ToString(), outputJson, result.FailedReason)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track step completion.");
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
                var controlAfterPending = await ResolveTerminationStatusAsync(ctx.RunId).ConfigureAwait(false);
                if (controlAfterPending is not null)
                {
                    await TryCompleteRunAsync(ctx.RunId, controlAfterPending).ConfigureAwait(false);
                    return result.Result;
                }

                var retryDelay = result.DelayNextStep ?? TimeSpan.FromSeconds(10);
                step.ScheduledTime = DateTimeOffset.UtcNow + retryDelay;
                _backgroundJobClient.Schedule<IHangfireStepRunner>(
                    runner => runner.RunStepAsync(ctx, flow, step, null),
                    retryDelay);

                await RecordEventAsync(ctx, flow, step, "step.pending", $"Step '{step.Key}' pending for {retryDelay}.")
                    .ConfigureAwait(false);
                return result.Result;
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
        finally
        {
            _contextAccessor.CurrentContext = null;
        }
    }

    public async ValueTask<object?> TriggerByScheduleAsync(Guid flowId, string triggerKey, PerformContext? performContext = null)
    {
        var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flow = flows.FirstOrDefault(f => f.Id == flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} not found.");

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger(triggerKey, "Cron", null)
        };

        return await TriggerAsync(ctx, performContext).ConfigureAwait(false);
    }

    public async ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, PerformContext? performContext = null)
    {
        var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flow = flows.FirstOrDefault(f => f.Id == flowId)
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

        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        ctx.TriggerData = await _outputsRepository.GetTriggerDataAsync(runId).ConfigureAwait(false);
        ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(runId).ConfigureAwait(false);

        return await RunStepAsync(ctx, flow, step, performContext).ConfigureAwait(false);
    }

    private async Task RunLegacySequentialContinuationAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, IStepResult result)
    {
        var next = await _flowExecutor.GetNextStep(ctx, flow, step, result).ConfigureAwait(false);
        if (next is not null)
        {
            if (result.DelayNextStep.HasValue)
            {
                next.ScheduledTime = DateTimeOffset.UtcNow + result.DelayNextStep.Value;
                _backgroundJobClient.Schedule<IHangfireStepRunner>(
                    runner => runner.RunStepAsync(ctx, flow, next, null),
                    result.DelayNextStep.Value);
            }
            else
            {
                _backgroundJobClient.Enqueue<IHangfireStepRunner>(
                    runner => runner.RunStepAsync(ctx, flow, next, null));
            }

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
            foreach (var readyStepKey in evaluation.ReadyStepKeys)
            {
                var metadata = flow.Manifest.Steps.FindStep(readyStepKey);
                if (metadata is null)
                {
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
            var hasFailed = statuses.Values.Any(x => x == StepStatus.Failed);
            termination = hasFailed ? StepStatus.Failed.ToString() : StepStatus.Succeeded.ToString();
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
        if (_runtimeStore is not null)
        {
            var claimed = await _runtimeStore.TryClaimStepAsync(ctx.RunId, step.Key).ConfigureAwait(false);
            if (!claimed)
            {
                return false;
            }
        }

        if (delay.HasValue)
        {
            step.ScheduledTime = DateTimeOffset.UtcNow + delay.Value;
            _backgroundJobClient.Schedule<IHangfireStepRunner>(
                runner => runner.RunStepAsync(ctx, flow, step, null),
                delay.Value);
        }
        else
        {
            _backgroundJobClient.Enqueue<IHangfireStepRunner>(
                runner => runner.RunStepAsync(ctx, flow, step, null));
        }

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
            _logger.LogWarning(ex, "Failed to mark step {StepKey} as skipped due to terminal run status.", step.Key);
        }
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
            _logger.LogWarning(ex, "Failed to record flow event {EventType}.", type);
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
