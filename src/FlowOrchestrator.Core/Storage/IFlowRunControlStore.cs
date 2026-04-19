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
    /// Marks the run as timed out. Called by the timeout-enforcement background service.
    /// </summary>
    /// <returns><see langword="true"/> if the record was found and updated; <see langword="false"/> otherwise.</returns>
    Task<bool> MarkTimedOutAsync(Guid runId, string? reason);

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
