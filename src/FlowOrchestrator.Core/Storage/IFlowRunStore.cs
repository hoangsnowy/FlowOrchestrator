namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persistence contract for flow run lifecycle: creating runs, recording step progress,
/// completing runs, and querying run history.
/// </summary>
public interface IFlowRunStore
{
    /// <summary>
    /// Creates a new run record in <c>Running</c> status and returns it.
    /// Called once per <c>TriggerAsync</c> invocation.
    /// </summary>
    Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId);

    /// <summary>
    /// Records the start of a step attempt: sets status to <c>Running</c>, persists inputs, and associates the Hangfire job ID.
    /// </summary>
    Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId);

    /// <summary>
    /// Records the outcome of a step attempt: updates status, persists output JSON, and sets error message on failure.
    /// </summary>
    Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage);

    /// <summary>
    /// Marks the run as complete (status: <c>Succeeded</c>, <c>Failed</c>, or <c>Cancelled</c>)
    /// and sets <see cref="FlowRunRecord.CompletedAt"/>.
    /// </summary>
    Task CompleteRunAsync(Guid runId, string status);

    /// <summary>
    /// Returns a page of run records, optionally filtered by <paramref name="flowId"/>.
    /// Results are ordered by start time descending.
    /// </summary>
    Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50);

    /// <summary>
    /// Returns a paginated run list with total count, supporting filtering by flow, status, and search text.
    /// </summary>
    Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId = null, string? status = null, int skip = 0, int take = 50, string? search = null);

    /// <summary>
    /// Returns full run detail including all step records and their attempt history,
    /// or <see langword="null"/> if no run with <paramref name="runId"/> exists.
    /// </summary>
    Task<FlowRunRecord?> GetRunDetailAsync(Guid runId);

    /// <summary>Returns aggregate counts used by the dashboard overview panel.</summary>
    Task<DashboardStatistics> GetStatisticsAsync();

    /// <summary>Returns all runs currently in <c>Running</c> status (used for timeout enforcement).</summary>
    Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync();

    /// <summary>
    /// Resets a step back to a re-runnable state so it can be re-enqueued by the retry flow.
    /// Clears <c>Running</c>/<c>Failed</c> status and increments attempt count.
    /// </summary>
    Task RetryStepAsync(Guid runId, string stepKey);

    /// <summary>
    /// Atomically records that a step has been dispatched for execution.
    /// Returns <see langword="true"/> if this is the first dispatch for this step in this run;
    /// <see langword="false"/> if the step was already dispatched (idempotent guard — caller should skip).
    /// </summary>
    /// <remarks>
    /// Must use an atomic INSERT-if-not-exists primitive (SQL <c>WHERE NOT EXISTS</c>,
    /// PostgreSQL <c>ON CONFLICT DO NOTHING</c>, or <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryAdd"/>).
    /// </remarks>
    Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default);

    /// <summary>
    /// Stores the runtime job or message ID returned by the dispatcher alongside the dispatch record.
    /// Best-effort — implementations should not throw; failures are silently ignored by the engine.
    /// </summary>
    Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default);

    /// <summary>
    /// Removes the dispatch record for a step, allowing it to be re-dispatched.
    /// Called by the engine before rescheduling a <see cref="StepStatus.Pending"/> (polling) step.
    /// </summary>
    Task ReleaseDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default);

    /// <summary>
    /// Returns the set of step keys that have been dispatched (and not yet released) for a run.
    /// Used by the recovery service to avoid re-dispatching already-in-flight steps.
    /// </summary>
    Task<IReadOnlySet<string>> GetDispatchedStepKeysAsync(Guid runId);
}
