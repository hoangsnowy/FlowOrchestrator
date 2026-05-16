using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Trigger-lifecycle partial of <see cref="FlowOrchestratorEngine"/>:
/// <see cref="TriggerAsync"/>, <see cref="TriggerByScheduleAsync"/>, idempotency-key handling,
/// and run-timeout resolution.
/// </summary>
public sealed partial class FlowOrchestratorEngine
{
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
                return new { runId = default(Guid?), disabled = true };
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
            catch (Exception ex) when (ex is not OperationCanceledException)
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

            await PublishEventSafelyAsync(new RunStartedEvent
            {
                RunId = triggerContext.RunId,
                FlowId = triggerContext.Flow.Id,
                FlowName = triggerContext.Flow.GetType().Name,
                TriggerKey = triggerContext.Trigger.Key
            }, ct).ConfigureAwait(false);

            return new { runId = triggerContext.RunId, duplicate = false };
        }
        catch (Exception ex)
        {
            // Mark the trigger activity as failed before propagating. Without this, APM tools
            // see a span that simply ended with no error indicator and never raise an alert.
            activity?.RecordError(ex);
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
}
