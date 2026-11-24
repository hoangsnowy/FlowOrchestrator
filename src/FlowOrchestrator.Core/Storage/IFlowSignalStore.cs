namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persistence contract for <c>WaitForSignal</c> step waiters: parked steps that resume only when an
/// external signal is delivered via the dashboard signal endpoint or programmatic call.
/// </summary>
/// <remarks>
/// Backends must guarantee atomic delivery semantics: a single signal is either persisted exactly once
/// on the matching live waiter row, or rejected as <see cref="SignalDeliveryStatus.AlreadyDelivered"/>,
/// <see cref="SignalDeliveryStatus.Expired"/> or <see cref="SignalDeliveryStatus.NotFound"/>. The
/// in-memory backend uses <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}.TryUpdate"/>;
/// SQL backends use a conditional <c>UPDATE … WHERE DeliveredAt IS NULL AND (ExpiresAt IS NULL OR ExpiresAt &gt; now)</c>
/// with row-count detection.
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
    /// <see cref="SignalDeliveryStatus.Expired"/> when every undelivered matching waiter is past its
    /// <see cref="FlowSignalWaiter.ExpiresAt"/>;
    /// <see cref="SignalDeliveryStatus.AlreadyDelivered"/> when every matching waiter already holds a payload.
    /// </returns>
    /// <remarks>
    /// <para>The payload goes to the oldest (<see cref="FlowSignalWaiter.CreatedAt"/>) waiter that is both
    /// undelivered and <b>live</b> — <see cref="FlowSignalWaiter.ExpiresAt"/> is <see langword="null"/> or later
    /// than the store's current time. An expired waiter never receives a payload and never blocks a live waiter
    /// that shares its signal name. Expiry is judged by the <b>store's</b> clock in the same atomic write that
    /// records the delivery (<c>SYSDATETIMEOFFSET()</c> on SQL Server, <c>now()</c> on PostgreSQL, the injected
    /// <see cref="TimeProvider"/> in memory), with the same inclusive boundary the <c>WaitForSignal</c> handler
    /// uses (<c>now &gt;= ExpiresAt</c> is expired), so a recorded <see cref="FlowSignalWaiter.DeliveredAt"/> is
    /// always earlier than the deadline.</para>
    /// <para>Before 2.0 the stores ignored <see cref="FlowSignalWaiter.ExpiresAt"/>: a signal landing between the
    /// deadline and the step's safety-net invocation (deadline + 1 s, later under load) was recorded and resumed
    /// the step past its timeout. Custom stores must apply the same rule.</para>
    /// </remarks>
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
