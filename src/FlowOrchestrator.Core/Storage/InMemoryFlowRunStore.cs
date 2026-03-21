using System.Collections.Concurrent;

namespace FlowOrchestrator.Core.Storage;

public sealed class InMemoryFlowRunStore : IFlowRunStore
{
    private readonly ConcurrentDictionary<Guid, FlowRunRecord> _runs = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), FlowStepRecord> _steps = new();

    public Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId)
    {
        var record = new FlowRunRecord
        {
            Id = runId,
            FlowId = flowId,
            FlowName = flowName,
            Status = "Running",
            TriggerKey = triggerKey,
            TriggerDataJson = triggerData,
            BackgroundJobId = jobId,
            StartedAt = DateTimeOffset.UtcNow
        };
        _runs[runId] = record;
        return Task.FromResult(record);
    }

    public Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId)
    {
        _steps[(runId, stepKey)] = new FlowStepRecord
        {
            RunId = runId,
            StepKey = stepKey,
            StepType = stepType,
            InputJson = inputJson,
            JobId = jobId,
            Status = "Running",
            StartedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
    {
        if (_steps.TryGetValue((runId, stepKey), out var step))
        {
            step.Status = status;
            step.OutputJson = outputJson;
            step.ErrorMessage = errorMessage;
            step.CompletedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task CompleteRunAsync(Guid runId, string status)
    {
        if (_runs.TryGetValue(runId, out var run))
        {
            run.Status = status;
            run.CompletedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50)
    {
        IReadOnlyList<FlowRunRecord> result = ApplyRunsFilter(flowId, null, null)
            .OrderByDescending(r => r.StartedAt)
            .Skip(skip).Take(take)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(
        Guid? flowId = null,
        string? status = null,
        int skip = 0,
        int take = 50,
        string? search = null)
    {
        var filtered = ApplyRunsFilter(flowId, status, search).OrderByDescending(r => r.StartedAt).ToList();
        var totalCount = filtered.Count;
        IReadOnlyList<FlowRunRecord> runs = filtered.Skip(skip).Take(take).ToList();
        return Task.FromResult((runs, totalCount));
    }

    public Task<FlowRunRecord?> GetRunDetailAsync(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out var run))
            return Task.FromResult<FlowRunRecord?>(null);

        var steps = _steps.Values
            .Where(s => s.RunId == runId)
            .OrderBy(s => s.StartedAt)
            .ToList();

        run.Steps = steps;
        return Task.FromResult<FlowRunRecord?>(run);
    }

    public Task<DashboardStatistics> GetStatisticsAsync()
    {
        var today = DateTimeOffset.UtcNow.Date;
        var stats = new DashboardStatistics
        {
            TotalFlows = _runs.Values.Select(r => r.FlowId).Distinct().Count(),
            ActiveRuns = _runs.Values.Count(r => r.Status == "Running"),
            CompletedToday = _runs.Values.Count(r => r.CompletedAt?.Date == today && r.Status == "Succeeded"),
            FailedToday = _runs.Values.Count(r => r.CompletedAt?.Date == today && r.Status == "Failed")
        };
        return Task.FromResult(stats);
    }

    public Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync()
    {
        IReadOnlyList<FlowRunRecord> result = _runs.Values
            .Where(r => r.Status == "Running")
            .OrderByDescending(r => r.StartedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task RetryStepAsync(Guid runId, string stepKey)
    {
        if (_steps.TryGetValue((runId, stepKey), out var step))
        {
            step.Status = "Running";
            step.OutputJson = null;
            step.ErrorMessage = null;
            step.CompletedAt = null;
            step.StartedAt = DateTimeOffset.UtcNow;
        }

        if (_runs.TryGetValue(runId, out var run))
        {
            run.Status = "Running";
            run.CompletedAt = null;
        }

        return Task.CompletedTask;
    }

    private IEnumerable<FlowRunRecord> ApplyRunsFilter(Guid? flowId, string? status, string? search)
    {
        var query = _runs.Values.AsEnumerable();

        if (flowId.HasValue)
            query = query.Where(r => r.FlowId == flowId.Value);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(r => MatchesRunSearch(r, search));
        }

        return query;
    }

    private bool MatchesRunSearch(FlowRunRecord run, string search)
    {
        if (ContainsIgnoreCase(run.Id.ToString(), search)
            || ContainsIgnoreCase(run.FlowName, search)
            || ContainsIgnoreCase(run.TriggerKey, search)
            || ContainsIgnoreCase(run.Status, search)
            || ContainsIgnoreCase(run.BackgroundJobId, search))
        {
            return true;
        }

        return _steps.Values.Any(s =>
            s.RunId == run.Id
            && (ContainsIgnoreCase(s.StepKey, search)
                || ContainsIgnoreCase(s.ErrorMessage, search)
                || ContainsIgnoreCase(s.OutputJson, search)));
    }

    private static bool ContainsIgnoreCase(string? value, string search)
    {
        return !string.IsNullOrEmpty(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}
