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
}
