using FlowOrchestrator.Core.Abstractions;
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

        await PublishEventSafelyAsync(new RunCompletedEvent
        {
            RunId = runId,
            Status = status
        }, default).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes <paramref name="evt"/> through <see cref="IFlowEventNotifier"/>, swallowing any
    /// exception. Telemetry must NEVER abort a flow — a misbehaving notifier (slow channel,
    /// disposed broadcaster, transient backplane error) is logged and ignored.
    /// </summary>
    private async ValueTask PublishEventSafelyAsync(FlowLifecycleEvent evt, CancellationToken ct)
    {
        try
        {
            await _eventNotifier.PublishAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EngineLog.EventPersistenceFailed(_logger, ex, type);
        }
    }
}
