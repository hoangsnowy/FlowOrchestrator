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
    /// Invoked from two places: lazily by the engine when a step is dispatched after the deadline has
    /// passed, and proactively by the periodic timeout-enforcement hosted service
    /// (<c>FlowTimeoutEnforcementHostedService</c>). Also sets the cancellation latch so in-flight
    /// steps short-circuit on their next dispatch — a fresh execution window can be granted afterwards
    /// via <see cref="ExtendDeadlineAsync"/>.
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
}
