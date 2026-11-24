using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
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

    /// <summary>
    /// Roles allowed to deliver this signal over the dashboard signal endpoint; the caller needs any one
    /// of them. Setting this (or <see cref="AuthorizationPolicy"/>) makes the signal <i>restricted</i>.
    /// </summary>
    /// <remarks>
    /// <para>Enforced by the dashboard's <c>POST /api/runs/{runId}/signals/{signalName}</c> endpoint, never by
    /// this handler and never by <see cref="IFlowSignalDispatcher"/>: an in-process caller is trusted, and the
    /// host owns authorization on that path. A restricted signal can only be delivered over HTTP by an identity
    /// authenticated through <c>FlowDashboard:Authorization</c>; Basic Auth and anonymous callers are refused,
    /// and the <c>flow-operator</c> role does not bypass the check.</para>
    /// <para>Must be a static list in the manifest (no expressions), and the step's <see cref="SignalName"/>
    /// must then be static too, so the endpoint can tell which step a signal addresses before it writes
    /// anything. The dashboard validates both when it maps its endpoints.</para>
    /// </remarks>
    public string[]? AllowedRoles { get; set; }

    /// <summary>
    /// Name of an ASP.NET Core authorization policy (registered with <c>AddAuthorization</c>) the caller must
    /// satisfy to deliver this signal over the dashboard signal endpoint. When <see cref="AllowedRoles"/> is
    /// set as well, the caller must satisfy both.
    /// </summary>
    /// <remarks>Same enforcement point and static-value rule as <see cref="AllowedRoles"/>; an unregistered
    /// policy name fails when the dashboard maps its endpoints.</remarks>
    public string? AuthorizationPolicy { get; set; }
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
/// <para>A signal that arrives at or after the deadline never resumes the step: the store refuses it with
/// <see cref="SignalDeliveryStatus.Expired"/>, and a payload some older writer recorded after the deadline is
/// treated as a timeout rather than a success.</para>
/// </remarks>
public sealed class WaitForSignalHandler : IStepHandler<WaitForSignalInput>
{
    /// <summary>The step type name the built-in handler is registered under.</summary>
    public const string StepTypeName = "WaitForSignal";

    private readonly IFlowSignalStore _signalStore;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _indefiniteParkInterval;

    /// <summary>Initialises the handler with its dependencies.</summary>
    /// <param name="signalStore">Persistence for waiters and delivered payloads.</param>
    /// <param name="clock">Optional clock; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">
    /// Optional signal options. Supplies <see cref="FlowSignalOptions.IndefiniteParkInterval"/> —
    /// the safety-net re-invocation interval used when the step declares no <c>timeoutSeconds</c>.
    /// Omitted (or non-positive) falls back to <see cref="FlowSignalOptions.DefaultIndefiniteParkInterval"/>;
    /// values above <see cref="FlowSignalOptions.MaxIndefiniteParkInterval"/> are clamped to it.
    /// </param>
    public WaitForSignalHandler(IFlowSignalStore signalStore, TimeProvider? clock = null, FlowSignalOptions? options = null)
    {
        _signalStore = signalStore;
        _clock = clock ?? TimeProvider.System;
        var configured = options?.IndefiniteParkInterval ?? FlowSignalOptions.DefaultIndefiniteParkInterval;
        _indefiniteParkInterval = configured <= TimeSpan.Zero
            ? FlowSignalOptions.DefaultIndefiniteParkInterval
            : configured > FlowSignalOptions.MaxIndefiniteParkInterval
                ? FlowSignalOptions.MaxIndefiniteParkInterval
                : configured;
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
        if (waiter is { DeliveredAt: { } deliveredAt })
        {
            // A payload recorded at or after the deadline was not received within the timeout, so it
            // never makes the step succeed. Built-in stores refuse such a delivery in the write itself
            // (SignalDeliveryStatus.Expired, judged by the store's clock in the statement that stamps
            // DeliveredAt), so for them DeliveredAt < ExpiresAt always holds and this guard cannot fail a
            // delivery the store accepted, whatever the app/database clock skew. It catches rows the store
            // did not vet: written by a pre-fix replica during a rolling upgrade, or by a custom store that
            // ignores ExpiresAt. DeliveredAt is the store's clock and ExpiresAt the app's, so for those rows
            // the guard is exact only up to that skew.
            if (waiter.ExpiresAt is { } deadline && deliveredAt >= deadline)
            {
                return TimedOut(step.Key, waiter, deadline, input);
            }

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
        // The store refuses a delivery to this waiter from the same instant on, judged by its own clock,
        // so once this branch fails the step no later signal can be recorded for it. The one residual
        // window is app/database clock skew with the database behind: the store keeps accepting for that
        // long past the deadline. The safety-net invocation runs 1 s after the deadline, so the window is
        // only reachable when the skew exceeds that margin or a stale re-invocation lands inside it; the
        // caller is then told Delivered while the step stays Failed (its claim is never released on a
        // terminal status, so the resume nudge cannot re-run it).
        if (waiter is { ExpiresAt: { } expiresAt } && now >= expiresAt)
        {
            return TimedOut(step.Key, waiter, expiresAt, input);
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
                : _indefiniteParkInterval;

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
            : _indefiniteParkInterval;
        return new StepResult
        {
            Key = step.Key,
            Status = StepStatus.Pending,
            DelayNextStep = remainingDelay
        };
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static StepResult TimedOut(string stepKey, FlowSignalWaiter waiter, DateTimeOffset expiresAt, WaitForSignalInput input)
    {
        // FlowSignalWaiter.CreatedAt is stamped by the STORE's clock (the injected TimeProvider in
        // memory, SYSDATETIMEOFFSET() / NOW() in the SQL backends) while ExpiresAt was computed from the
        // handler's TimeProvider. Subtracting the two mixes clocks: under ordinary app/database skew the
        // reported window is off by the skew, and under an injected or frozen TimeProvider it can even
        // come out negative. Report the configured timeout instead, falling back to the stored window
        // (clamped at zero) only when the step no longer declares one.
        var elapsed = input.TimeoutSeconds is { } configuredSeconds && configuredSeconds > 0
            ? configuredSeconds
            : Math.Max(0, (int)Math.Round((expiresAt - waiter.CreatedAt).TotalSeconds, MidpointRounding.AwayFromZero));
        return new StepResult
        {
            Key = stepKey,
            Status = StepStatus.Failed,
            FailedReason = $"Signal '{waiter.SignalName}' not received within {elapsed}s."
        };
    }

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
