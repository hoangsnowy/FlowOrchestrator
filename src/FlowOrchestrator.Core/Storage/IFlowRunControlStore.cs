namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persistence contract for run-level control signals: cancellation, timeout, and idempotency.
/// Decoupled from <see cref="IFlowRunStore"/> so control records can be written before the run is committed.
/// </summary>
public interface IFlowRunControlStore
{
    /// <summary>
    /// Persists the control record for a new run, including an optional idempotency key
    /// and an absolute timeout deadline.
    /// </summary>
    Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc);

    /// <summary>
    /// Returns the control record for the given run, or <see langword="null"/> if not found.
    /// </summary>
    Task<FlowRunControlRecord?> GetRunControlAsync(Guid runId);

    /// <summary>
    /// Marks a cancellation request for the run. Steps check this flag before executing.
    /// </summary>
    /// <returns><see langword="true"/> if the record was found and updated; <see langword="false"/> otherwise.</returns>
    Task<bool> RequestCancelAsync(Guid runId, string? reason);

    /// <summary>
    /// Marks the run as timed out.
    /// </summary>
    /// <returns><see langword="true"/> if the record was found and updated; <see langword="false"/> otherwise.</returns>
    /// <remarks>
    /// <para>
    /// Applies only while the timeout is not yet latched <b>and no cancel has been requested</b>: a user
    /// cancel recorded first wins, so the run stays <c>Cancelled</c> and keeps its reason. Without that
    /// guard a cancel landing between the caller's deadline check and this write was relabelled a timeout,
    /// and a later retry's <see cref="ExtendDeadlineAsync"/> then cleared the user's cancel. When
    /// <paramref name="reason"/> is <see langword="null"/>, an existing cancel reason is kept, falling back
    /// to <c>"Run timed out."</c>.
    /// </para>
    /// <para>
    /// Invoked from two places: lazily by the engine when a step is dispatched after the deadline has
    /// passed, and proactively by the periodic timeout-enforcement hosted service
    /// (<c>FlowTimeoutEnforcementHostedService</c>). Also sets the cancellation latch so in-flight
    /// steps short-circuit on their next dispatch — a fresh execution window can be granted afterwards
    /// via <see cref="ExtendDeadlineAsync"/>.
    /// </para>
    /// </remarks>
    Task<bool> MarkTimedOutAsync(Guid runId, string? reason);

    /// <summary>
    /// Grants a run a fresh execution window: sets <c>TimeoutAtUtc</c> to <paramref name="newTimeoutAtUtc"/>
    /// (or clears the deadline when <see langword="null"/>) and un-latches any <em>timeout-induced</em>
    /// termination — clears <c>TimedOutAtUtc</c> and the cancellation fields that
    /// <see cref="MarkTimedOutAsync"/> set. A genuine user cancellation (recorded via
    /// <see cref="RequestCancelAsync"/> while the run had not timed out) is preserved.
    /// </summary>
    /// <param name="runId">The run whose deadline is being refreshed.</param>
    /// <param name="newTimeoutAtUtc">
    /// The new absolute deadline, or <see langword="null"/> to leave the run without a timeout bound.
    /// </param>
    /// <returns><see langword="true"/> if the record was found and updated; <see langword="false"/> otherwise.</returns>
    /// <remarks>
    /// Called by <c>FlowOrchestratorEngine.RetryStepAsync</c> before re-dispatch so a step retried after
    /// the run's deadline lapsed can actually re-execute instead of being skipped by the termination gate.
    /// Default implementation is a no-op returning <see langword="false"/> so existing custom
    /// <see cref="IFlowRunControlStore"/> implementations continue to compile.
    /// </remarks>
    Task<bool> ExtendDeadlineAsync(Guid runId, DateTimeOffset? newTimeoutAtUtc) => Task.FromResult(false);

    /// <summary>
    /// Looks up an existing run that was started with the given idempotency key.
    /// Returns the RunId of the existing run, or <see langword="null"/> if none exists.
    /// </summary>
    Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey);

    /// <summary>
    /// Atomically registers an idempotency key for the given run.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the key was registered by this call;
    /// <see langword="false"/> if a record with this key already existed (duplicate trigger).
    /// </returns>
    Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId);

    /// <summary>
    /// Removes the idempotency-key registration for <paramref name="runId"/>, so a later trigger with the
    /// same key starts a new run. A registration that maps the key to a different run is left alone.
    /// </summary>
    /// <param name="flowId">The flow the key was registered for.</param>
    /// <param name="triggerKey">The trigger the key was registered for.</param>
    /// <param name="idempotencyKey">The caller-supplied idempotency key.</param>
    /// <param name="runId">The run the registration must still point at for it to be removed.</param>
    /// <returns><see langword="true"/> when a registration was removed.</returns>
    /// <remarks>
    /// Called by the engine only when a trigger registered the key and then failed before dispatching any
    /// entry step: without it a caller retrying the failed delivery with the same key was answered
    /// "duplicate" of a run that never started work. Default implementation is a no-op returning
    /// <see langword="false"/> so existing custom stores keep compiling; such a store keeps the 1.x
    /// behaviour (the retry is absorbed as a duplicate of the dead run).
    /// </remarks>
    Task<bool> ReleaseIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
        => Task.FromResult(false);

    /// <summary>
    /// Re-points an idempotency reservation from <paramref name="staleRunId"/> to <paramref name="newRunId"/>
    /// when the reservation is stale and its run never started work, and returns the run the key maps to
    /// once the call is over.
    /// </summary>
    /// <param name="flowId">The flow the key was registered for.</param>
    /// <param name="triggerKey">The trigger the key was registered for.</param>
    /// <param name="idempotencyKey">The caller-supplied idempotency key, normalised like every other key member.</param>
    /// <param name="staleRunId">The run the registration must still point at for the takeover to apply.</param>
    /// <param name="newRunId">The run of the trigger asking to take the key over.</param>
    /// <param name="reservationTimeout">
    /// Minimum age of the registration, measured on the clock that stamped it (the database clock on
    /// SQL Server / PostgreSQL), for it to count as stale.
    /// </param>
    /// <returns>
    /// <paramref name="newRunId"/> when this call took the key over; otherwise the run the key maps to now
    /// (<paramref name="staleRunId"/>, or the run of a concurrent caller that won the takeover), or
    /// <see langword="null"/> when no registration exists for the key any more.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <c>TriggerAsync</c> registers the key before it creates the run. A process killed between the two
    /// left the key pointing at a run that never started, so every retry of that delivery was answered
    /// "duplicate" of a dead run and the delivery was lost. A duplicate trigger calls this to take the key
    /// over in that case only.
    /// </para>
    /// <para>
    /// The takeover applies only when, all decided in <b>one atomic operation</b>: the key still maps to
    /// <paramref name="staleRunId"/>; the registration is at least <paramref name="reservationTimeout"/>
    /// old; and the stale run has <b>no dispatch-ledger row and no step status row</b> (it never started
    /// work). It re-stamps the registration time, so the new reservation gets its own window. A check
    /// made separately from the write lets two callers both take the key over, or takes it from a run
    /// that dispatched its first step in between; exactly one of any number of concurrent callers may win.
    /// </para>
    /// <para>
    /// In the same atomic operation the takeover sets the stale run's cancel latch, creating its control
    /// record when it has none, so a trigger that was only slow (not dead) and dispatches afterwards has
    /// its steps skipped by the termination gate instead of running beside the new run. The record may
    /// outlive the run row it belongs to when that run row was never written; it holds no deadline and is
    /// never swept. Completing the stale run is left to the engine.
    /// </para>
    /// <para>
    /// No default implementation: a store that silently never took over would hide the lost delivery.
    /// </para>
    /// </remarks>
    Task<Guid?> TryTakeOverStaleIdempotencyKeyAsync(
        Guid flowId,
        string triggerKey,
        string idempotencyKey,
        Guid staleRunId,
        Guid newRunId,
        TimeSpan reservationTimeout);
}
