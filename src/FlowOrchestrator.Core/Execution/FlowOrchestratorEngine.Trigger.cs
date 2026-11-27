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
        using var _scope = EngineLogScope.Begin(_logger, triggerContext.RunId, triggerContext.Flow.Id, stepKey: null, attempt: null,
            flowName: triggerContext.Flow.GetType().Name, triggerKey: triggerContext.Trigger.Key);

        activity?.SetTag("flow.id", triggerContext.Flow.Id.ToString());
        activity?.SetTag("flow.name", triggerContext.Flow.GetType().Name);
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
                return new FlowTriggerResult(null, Disabled: true);
            }

            var idempotencyKey = TryGetIdempotencyKey(triggerContext.TriggerHeaders);
            var registeredIdempotencyKey = false;
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existingRunId = await _runControlStore
                    .FindRunIdByIdempotencyKeyAsync(triggerContext.Flow.Id, triggerContext.Trigger.Key, idempotencyKey)
                    .ConfigureAwait(false);
                if (existingRunId.HasValue)
                {
                    triggerContext.RunId = existingRunId.Value;
                    activity?.SetTag("duplicate", true);
                    return new FlowTriggerResult(existingRunId.Value, Duplicate: true);
                }

                var registered = await _runControlStore
                    .TryRegisterIdempotencyKeyAsync(triggerContext.Flow.Id, triggerContext.Trigger.Key, idempotencyKey, triggerContext.RunId)
                    .ConfigureAwait(false);
                registeredIdempotencyKey = registered;
                if (!registered)
                {
                    existingRunId = await _runControlStore
                        .FindRunIdByIdempotencyKeyAsync(triggerContext.Flow.Id, triggerContext.Trigger.Key, idempotencyKey)
                        .ConfigureAwait(false);

                    if (existingRunId.HasValue)
                    {
                        triggerContext.RunId = existingRunId.Value;
                        activity?.SetTag("duplicate", true);
                        return new FlowTriggerResult(existingRunId.Value, Duplicate: true);
                    }
                }
            }

            var anyEntryScheduled = false;
            var anyEntrySkipped = false;
            List<string>? skippedEntryKeys = null;
            try
            {
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

                var timeoutAt = ResolveTimeoutAtUtc(triggerContext.TriggerData);
                await _runControlStore.ConfigureRunAsync(
                    triggerContext.RunId,
                    triggerContext.Flow.Id,
                    triggerContext.Trigger.Key,
                    idempotencyKey,
                    timeoutAt).ConfigureAwait(false);

                await _outputsRepository.SaveTriggerDataAsync(triggerContext, triggerContext.Flow, triggerContext.Trigger).ConfigureAwait(false);
                await _outputsRepository.SaveTriggerHeadersAsync(triggerContext, triggerContext.Flow, triggerContext.Trigger).ConfigureAwait(false);

                var entries = _graphPlanner.CreateEntrySteps(triggerContext);
                if (entries.Count == 0)
                {
                    throw new InvalidOperationException("No entry step found for flow.");
                }

                foreach (var entry in entries)
                {
                    var skipped = await TryEvaluateWhenAndSkipAsync(triggerContext, triggerContext.Flow, entry.Key).ConfigureAwait(false);
                    if (skipped)
                    {
                        anyEntrySkipped = true;
                        (skippedEntryKeys ??= []).Add(entry.Key);
                        triggerContext.EntryStepsStarted = true;
                        continue;
                    }

                    if (await TryScheduleStepAsync(triggerContext, triggerContext.Flow, entry, null).ConfigureAwait(false))
                    {
                        anyEntryScheduled = true;
                        triggerContext.EntryStepsStarted = true;
                    }
                }
            }
            catch (Exception) when (registeredIdempotencyKey && !anyEntryScheduled && !anyEntrySkipped)
            {
                // This trigger registered the idempotency key and then failed before any entry step was
                // dispatched or skipped (StartRunAsync, run control, trigger payload, or the entry dispatch
                // itself threw). Left alone, the key keeps answering "duplicate" of a run that will never
                // start work, so a caller retrying the failed delivery is silently absorbed. Neutralise the
                // dead run and release the key, then rethrow so the caller still sees the failure.
                // Catch-and-rethrow: never swallowed.
                await AbandonUnstartedRunAsync(triggerContext, idempotencyKey!).ConfigureAwait(false);
                throw;
            }

            // A When-skipped entry step is a status change no step completion will report: continue the graph
            // from it, so its dependents are classified now (a dependent that runs after a skip is dispatched,
            // one that needs Succeeded is cascade-skipped) and a run whose every entry was skipped terminates.
            // Since the continuation reads only the changed steps' frontier (#189 P0-1), this is also needed
            // when other entries were scheduled: no later continuation re-reads the whole graph before the run
            // is about to close. The gate cannot close the run under the scheduled entries — their ledger rows
            // have no status row yet, which reads as in-flight work.
            if (skippedEntryKeys is not null)
            {
                await RunGraphContinuationAsync(
                    triggerContext,
                    triggerContext.Flow,
                    CreateRunLevelStep(triggerContext.RunId),
                    new StepResult { Key = "__entry_skipped", Status = StepStatus.Skipped },
                    skippedEntryKeys).ConfigureAwait(false);
            }

            if (_observabilityOptions.EnableOpenTelemetry)
            {
                _telemetry.RunStartedCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("flow_id", triggerContext.Flow.Id.ToString()),
                    new KeyValuePair<string, object?>("trigger_key", triggerContext.Trigger.Key));
            }

            await PublishEventSafelyAsync(new RunStartedEvent
            {
                RunId = triggerContext.RunId,
                FlowId = triggerContext.Flow.Id,
                FlowName = triggerContext.Flow.GetType().Name,
                TriggerKey = triggerContext.Trigger.Key
            }, ct).ConfigureAwait(false);

            return new FlowTriggerResult(triggerContext.RunId);
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

    /// <summary>
    /// Compensates a trigger that registered its idempotency key and then failed before dispatching or
    /// skipping any entry step: cancels and fails the run it created, then releases the key so the
    /// caller's retry of the same delivery starts a new run instead of being answered "duplicate".
    /// </summary>
    /// <param name="triggerContext">The failed trigger; its run id is the one the key points at.</param>
    /// <param name="idempotencyKey">The key this trigger registered.</param>
    /// <remarks>
    /// <para>
    /// Releasing the key (rather than letting a duplicate re-drive the existing run) is the safer choice
    /// under concurrency: a duplicate arriving while the first trigger is still between its key
    /// registration and its trigger-payload write cannot tell a live start from a dead one, and re-driving
    /// would dispatch entry steps that read no payload. A duplicate that lands inside that window is still
    /// answered with the first run's id, which is the normal "in progress" answer of an idempotent API.
    /// </para>
    /// <para>
    /// Order matters. The cancel latch goes first, so an entry step whose dispatch threw ambiguously (the
    /// broker accepted the message, then the call failed) is skipped by the termination gate rather than
    /// running alongside the retry's run. The run is completed <c>Failed</c> next, so startup recovery does
    /// not resurrect it. The key is released last, so no retry can start before the dead run is inert.
    /// Each write is best-effort and logged: the caller rethrows the original failure either way. None of
    /// these store calls takes a cancellation token, so a caller that disconnects cannot interrupt them.
    /// </para>
    /// </remarks>
    private async Task AbandonUnstartedRunAsync(ITriggerContext triggerContext, string idempotencyKey)
    {
        var runId = triggerContext.RunId;
        try
        {
            await _runControlStore.RequestCancelAsync(runId, "Trigger failed before any step was dispatched.").ConfigureAwait(false);
            await _runStore.CompleteRunIfActiveAsync(runId, StepStatus.Failed.ToString()).ConfigureAwait(false);
            await _runControlStore
                .ReleaseIdempotencyKeyAsync(triggerContext.Flow.Id, triggerContext.Trigger.Key, idempotencyKey, runId)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort compensation on a path that is already failing: logged, never masks the original.
            EngineLog.IdempotencyKeyReleaseFailed(_logger, ex, runId);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A schedule for a trigger key the current manifest does not declare as a
    /// <see cref="TriggerType.Cron"/> trigger is rejected: logged (EventId 1012) and answered with a
    /// <see cref="FlowTriggerResult"/> whose <see cref="FlowTriggerResult.RunId"/> is <see langword="null"/>,
    /// without starting a run. Recurring jobs outlive the code that created them — Hangfire keeps
    /// <c>flow-{flowId}-{triggerKey}</c> in its storage and a Service Bus tick can already be in the broker —
    /// and the trigger sync only walks the triggers the manifest still declares, so a renamed or deleted cron
    /// trigger left its job firing real runs for ever. Not thrown, so a runtime does not retry the orphaned job.
    /// </remarks>
    public async ValueTask<object?> TriggerByScheduleAsync(Guid flowId, string triggerKey, string? jobId = null, CancellationToken ct = default)
    {
        var flow = _flows.Find(flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} not found.");

        // O(1) dictionary lookup per tick; the manifest is immutable after startup.
        if (!flow.Manifest.Triggers.TryGetValue(triggerKey, out var trigger) || trigger.Type != TriggerType.Cron)
        {
            EngineLog.StaleScheduleRejected(_logger, flowId, triggerKey, jobId);
            return new FlowTriggerResult(null);
        }

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
