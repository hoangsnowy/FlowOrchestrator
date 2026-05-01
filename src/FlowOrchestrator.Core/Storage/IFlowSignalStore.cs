namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persistence contract for <c>WaitForSignal</c> step waiters: parked steps that resume only when an
/// external signal is delivered via the dashboard signal endpoint or programmatic call.
/// </summary>
/// <remarks>
/// Backends must guarantee atomic delivery semantics: a single signal is either persisted exactly once
/// on the matching waiter row, or rejected as <see cref="SignalDeliveryStatus.AlreadyDelivered"/>. The
/// in-memory backend uses <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}.TryUpdate"/>;
/// SQL backends use a conditional <c>UPDATE … WHERE DeliveredAt IS NULL</c> with row-count detection.
/// </remarks>
public interface IFlowSignalStore
{
    /// <summary>
    /// Registers a new waiter for <paramref name="runId"/> + <paramref name="stepKey"/>. Idempotent —
    /// a duplicate register on the same key updates the signal name / expiry without resetting <c>CreatedAt</c>.
    /// </summary>
    /// <param name="runId">The run that owns the parked step.</param>
    /// <param name="stepKey">The step key as authored in the flow manifest.</param>
    /// <param name="signalName">Logical signal name to address the waiter from the signal endpoint.</param>
    /// <param name="expiresAt">Optional absolute deadline; <see langword="null"/> waits indefinitely.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RegisterWaiterAsync(
        Guid runId,
        string stepKey,
        string signalName,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically attempts to deliver a payload to the waiter matching (<paramref name="runId"/>, <paramref name="signalName"/>).
    /// </summary>
    /// <param name="runId">The run whose waiter should receive the payload.</param>
    /// <param name="signalName">Signal name to look up.</param>
    /// <param name="payloadJson">Pre-serialised JSON payload supplied by the caller.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="SignalDeliveryStatus.Delivered"/> with the resolved <c>StepKey</c> on success;
    /// <see cref="SignalDeliveryStatus.NotFound"/> when no waiter matches;
    /// <see cref="SignalDeliveryStatus.AlreadyDelivered"/> when a payload is already recorded.
    /// </returns>
    ValueTask<SignalDeliveryResult> DeliverSignalAsync(
        Guid runId,
        string signalName,
        string payloadJson,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the waiter for the given run + step key, or <see langword="null"/> if none is registered.
    /// </summary>
    ValueTask<FlowSignalWaiter?> GetWaiterAsync(
        Guid runId,
        string stepKey,
        CancellationToken ct = default);

    /// <summary>Removes the waiter row. Called by the handler when the wait is over (delivered, expired, or cancelled).</summary>
    ValueTask RemoveWaiterAsync(
        Guid runId,
        string stepKey,
        CancellationToken ct = default);
}
