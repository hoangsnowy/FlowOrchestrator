namespace FlowOrchestrator.Core.Storage;

public interface IFlowRunStore
{
    Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId);
    Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId);
    Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage);
    Task CompleteRunAsync(Guid runId, string status);
    Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50);
    Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId = null, string? status = null, int skip = 0, int take = 50);
    Task<FlowRunRecord?> GetRunDetailAsync(Guid runId);
    Task<DashboardStatistics> GetStatisticsAsync();
    Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync();
    Task RetryStepAsync(Guid runId, string stepKey);
}
