using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// <see cref="IFlowRunStore"/> decorator that forwards every member, default-implemented ones included,
/// to a real store, and lets a test inject a fault into, or hold, one specific write.
/// </summary>
/// <param name="inner">The real store every call is forwarded to.</param>
internal sealed class FaultInjectingRunStore(IFlowRunStore inner) : IFlowRunStore
{
    private Exception? _nextStartFault;
    private string? _nextStartFaultKey;

    /// <summary>
    /// Runs before every <see cref="RetryStepAsync"/> is forwarded; a test awaits a gate here to line two
    /// concurrent retries up in front of the store's conditional reset.
    /// </summary>
    public Func<Guid, string, Task>? BeforeRetryStep { get; set; }

    /// <summary>Makes the next <see cref="RecordStepStartAsync"/> for <paramref name="stepKey"/> throw <paramref name="fault"/>.</summary>
    public void FailNextStepStart(string stepKey, Exception fault)
    {
        _nextStartFaultKey = stepKey;
        _nextStartFault = fault;
    }

    public Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId, Guid? sourceRunId = null)
        => inner.StartRunAsync(flowId, flowName, runId, triggerKey, triggerData, jobId, sourceRunId);

    public Task<IReadOnlyList<FlowRunRecord>> GetDerivedRunsAsync(Guid sourceRunId) => inner.GetDerivedRunsAsync(sourceRunId);

    public Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId)
    {
        if (_nextStartFault is { } fault && string.Equals(_nextStartFaultKey, stepKey, StringComparison.Ordinal))
        {
            _nextStartFault = null;
            _nextStartFaultKey = null;
            return Task.FromException(fault);
        }

        return inner.RecordStepStartAsync(runId, stepKey, stepType, inputJson, jobId);
    }

    public Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
        => inner.RecordStepCompleteAsync(runId, stepKey, status, outputJson, errorMessage);

    public Task<bool> TryCompleteRunningStepAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
        => inner.TryCompleteRunningStepAsync(runId, stepKey, status, outputJson, errorMessage);

    public Task CompleteRunAsync(Guid runId, string status) => inner.CompleteRunAsync(runId, status);

    public Task<bool> CompleteRunIfActiveAsync(Guid runId, string status) => inner.CompleteRunIfActiveAsync(runId, status);

    public Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50)
        => inner.GetRunsAsync(flowId, skip, take);

    public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId = null, string? status = null, int skip = 0, int take = 50, string? search = null)
        => inner.GetRunsPageAsync(flowId, status, skip, take, search);

    public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId, string? status, int skip, int take, string? search, bool deepSearch, DateTimeOffset? startedFrom = null, DateTimeOffset? startedTo = null)
        => inner.GetRunsPageAsync(flowId, status, skip, take, search, deepSearch, startedFrom, startedTo);

    public Task<RunSearchPage> SearchRunsAsync(RunSearchQuery query, CancellationToken cancellationToken = default)
        => inner.SearchRunsAsync(query, cancellationToken);

    public Task<FlowRunRecord?> GetRunDetailAsync(Guid runId) => inner.GetRunDetailAsync(runId);

    public Task<FlowRunRecord?> GetRunAsync(Guid runId) => inner.GetRunAsync(runId);

    public Task<FlowStepRecord?> GetStepAsync(Guid runId, string stepKey) => inner.GetStepAsync(runId, stepKey);

    public Task<DashboardStatistics> GetStatisticsAsync() => inner.GetStatisticsAsync();

    public Task<IReadOnlyList<RunTimeseriesBucket>> GetRunTimeseriesAsync(RunTimeseriesGranularity granularity, DateTimeOffset since, DateTimeOffset until, Guid? flowId = null)
        => inner.GetRunTimeseriesAsync(granularity, since, until, flowId);

    public Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync() => inner.GetActiveRunsAsync();

    public async Task<bool> RetryStepAsync(Guid runId, string stepKey)
    {
        if (BeforeRetryStep is { } hook)
        {
            await hook(runId, stepKey);
        }

        return await inner.RetryStepAsync(runId, stepKey);
    }

    public Task ResetCascadeSkippedDependentsAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
        => inner.ResetCascadeSkippedDependentsAsync(runId, stepKeys);

    public Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
        => inner.TryRecordDispatchAsync(runId, stepKey, ct);

    public Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default)
        => inner.AnnotateDispatchAsync(runId, stepKey, jobId, ct);

    public Task ReleaseDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
        => inner.ReleaseDispatchAsync(runId, stepKey, ct);

    public Task<IReadOnlySet<string>> GetDispatchedStepKeysAsync(Guid runId) => inner.GetDispatchedStepKeysAsync(runId);

    public Task<bool> IsStepDispatchedAsync(Guid runId, string stepKey) => inner.IsStepDispatchedAsync(runId, stepKey);
}
