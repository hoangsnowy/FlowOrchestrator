using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Strongly-typed inputs for the built-in <c>WaitForSignal</c> step type.
/// </summary>
public sealed class WaitForSignalInput
{
    /// <summary>
    /// Logical signal name addressed by callers when posting to the signal endpoint.
    /// Must be unique among the <c>WaitForSignal</c> steps active in the same run.
    /// </summary>
    public string SignalName { get; set; } = "default";

    /// <summary>
    /// Maximum time to wait, in seconds. <c>null</c> or non-positive means wait indefinitely.
    /// When the deadline passes, the step transitions to <see cref="StepStatus.Failed"/>.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// Built-in handler for the <c>WaitForSignal</c> step type. Parks the step in
/// <see cref="StepStatus.Pending"/> until either an external signal is delivered via
/// <see cref="IFlowSignalStore.DeliverSignalAsync"/> or the configured timeout elapses.
/// </summary>
/// <remarks>
/// This handler is invoked at least three times in the happy path:
/// <list type="number">
///   <item>First invocation registers the waiter and returns <see cref="StepStatus.Pending"/>.</item>
///   <item>The signal endpoint persists a payload then nudges this step via <c>IStepDispatcher.ScheduleStepAsync</c>.</item>
///   <item>Second invocation observes <c>DeliveredAt</c> on the waiter and returns <see cref="StepStatus.Succeeded"/>.</item>
/// </list>
/// On timeout, the third invocation (scheduled during the first) observes the expired waiter and returns <see cref="StepStatus.Failed"/>.
/// </remarks>
public sealed class WaitForSignalHandler : IStepHandler<WaitForSignalInput>
{
    private static readonly TimeSpan IndefiniteParkInterval = TimeSpan.FromHours(24);

    private readonly IFlowSignalStore _signalStore;
    private readonly TimeProvider _clock;

    /// <summary>Initialises the handler with its dependencies.</summary>
    public WaitForSignalHandler(IFlowSignalStore signalStore, TimeProvider? clock = null)
    {
        _signalStore = signalStore;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async ValueTask<object?> ExecuteAsync(
        IExecutionContext context, IFlowDefinition flow, IStepInstance<WaitForSignalInput> step)
    {
        var input = step.Inputs ?? new WaitForSignalInput();
        if (string.IsNullOrWhiteSpace(input.SignalName))
        {
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Failed,
                FailedReason = "WaitForSignal step requires a non-empty 'signalName' input."
            };
        }

        var now = _clock.GetUtcNow();
        var waiter = await _signalStore.GetWaiterAsync(context.RunId, step.Key).ConfigureAwait(false);

        // ── Branch 1: signal already delivered → succeed and emit payload as output ───────────
        // The waiter row is intentionally NOT removed here. Leaving it as a tombstone makes the
        // handler idempotent if the engine re-invokes the step later (e.g. a stale polling
        // reschedule queued before delivery): subsequent invocations re-enter this branch and
        // return Succeeded again instead of re-registering a fresh waiter and re-parking.
        if (waiter is { DeliveredAt: not null })
        {
            var payload = ParsePayload(waiter.PayloadJson);
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Succeeded,
                Result = payload
            };
        }

        // ── Branch 2: waiter expired without delivery → fail with descriptive reason ──────────
        // Same idempotency reasoning: leave the row with ExpiresAt < now so a stale re-invocation
        // returns Failed again rather than registering a brand-new waiter.
        if (waiter is { ExpiresAt: { } expiresAt } && now >= expiresAt)
        {
            var elapsed = (int)Math.Round((expiresAt - waiter.CreatedAt).TotalSeconds, MidpointRounding.AwayFromZero);
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Failed,
                FailedReason = $"Signal '{waiter.SignalName}' not received within {elapsed}s."
            };
        }

        // ── Branch 3: not yet registered → register and pend (with timeout schedule if any) ───
        if (waiter is null)
        {
            DateTimeOffset? expiry = input.TimeoutSeconds is { } seconds && seconds > 0
                ? now + TimeSpan.FromSeconds(seconds)
                : null;

            await _signalStore
                .RegisterWaiterAsync(context.RunId, step.Key, input.SignalName.Trim(), expiry)
                .ConfigureAwait(false);

            // Park the step. If a timeout is configured, schedule the engine to re-invoke us
            // shortly after the deadline so we can observe the expiry. Otherwise park for a
            // long but bounded interval so the engine still considers the run live in metrics.
            var delay = expiry is { } at
                ? Max(at - now + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
                : IndefiniteParkInterval;

            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Pending,
                DelayNextStep = delay
            };
        }

        // ── Branch 4: already registered, neither delivered nor expired → keep waiting ───────
        var remainingDelay = waiter.ExpiresAt is { } absoluteExpiry
            ? Max(absoluteExpiry - now + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            : IndefiniteParkInterval;
        return new StepResult
        {
            Key = step.Key,
            Status = StepStatus.Pending,
            DelayNextStep = remainingDelay
        };
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static object? ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }
}
