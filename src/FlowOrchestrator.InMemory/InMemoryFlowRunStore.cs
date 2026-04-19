using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// Thread-safe in-memory implementation of all run storage interfaces.
/// Uses <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> for concurrent access.
/// All data is held in process memory and lost on restart.
/// </summary>
public sealed class InMemoryFlowRunStore :
    IFlowRunStore,
    IFlowRunRuntimeStore,
    IFlowRunControlStore,
    IFlowRetentionStore
{
    private readonly ConcurrentDictionary<Guid, FlowRunRecord> _runs = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), FlowStepRecord> _steps = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey, int Attempt), FlowStepAttemptRecord> _stepAttempts = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), int> _stepAttemptCounters = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), byte> _stepClaims = new();
    private readonly ConcurrentDictionary<Guid, FlowRunControlRecord> _runControls = new();
    private readonly ConcurrentDictionary<(Guid FlowId, string TriggerKey, string IdempotencyKey), Guid> _idempotency = new();

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
        var key = (runId, stepKey);
        var attempt = _stepAttemptCounters.AddOrUpdate(key, 1, static (_, current) => current + 1);
        var startedAt = DateTimeOffset.UtcNow;

        _stepAttempts[(runId, stepKey, attempt)] = new FlowStepAttemptRecord
        {
            RunId = runId,
            StepKey = stepKey,
            Attempt = attempt,
            StepType = stepType,
            Status = "Running",
            InputJson = inputJson,
            JobId = jobId,
            StartedAt = startedAt
        };

        _steps[key] = new FlowStepRecord
        {
            RunId = runId,
            StepKey = stepKey,
            StepType = stepType,
            InputJson = inputJson,
            OutputJson = null,
            ErrorMessage = null,
            JobId = jobId,
            Status = "Running",
            StartedAt = startedAt,
            CompletedAt = null,
            AttemptCount = attempt
        };

        return Task.CompletedTask;
    }

    public Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
    {
        var completedAt = DateTimeOffset.UtcNow;

        if (_steps.TryGetValue((runId, stepKey), out var step))
        {
            step.Status = status;
            step.OutputJson = outputJson;
            step.ErrorMessage = errorMessage;
            step.CompletedAt = completedAt;
        }

        var latestAttempt = _stepAttempts.Values
            .Where(a => a.RunId == runId && string.Equals(a.StepKey, stepKey, StringComparison.Ordinal))
            .OrderByDescending(a => a.Attempt)
            .FirstOrDefault();

        if (latestAttempt is not null)
        {
            latestAttempt.Status = status;
            latestAttempt.OutputJson = outputJson;
            latestAttempt.ErrorMessage = errorMessage;
            latestAttempt.CompletedAt = completedAt;
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
            .Select(CloneStepRecord)
            .ToList();

        var attempts = _stepAttempts.Values
            .Where(a => a.RunId == runId)
            .OrderBy(a => a.StartedAt)
            .ThenBy(a => a.Attempt)
            .Select(CloneStepAttemptRecord)
            .ToList();

        var attemptsLookup = attempts
            .GroupBy(a => a.StepKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FlowStepAttemptRecord>)g.OrderBy(a => a.Attempt).ToList(), StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (!attemptsLookup.TryGetValue(step.StepKey, out var stepAttempts) || stepAttempts.Count == 0)
            {
                stepAttempts = [CreateSyntheticAttempt(step)];
            }

            step.Attempts = stepAttempts;
            step.AttemptCount = stepAttempts.Count;
        }

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

    public Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId)
    {
        var map = _steps.Values
            .Where(s => s.RunId == runId)
            .GroupBy(s => s.StepKey, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => ParseStepStatus(g.OrderByDescending(x => x.StartedAt).First().Status),
                StringComparer.Ordinal);

        return Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(map);
    }

    public Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId)
    {
        IReadOnlyCollection<string> claimed = _stepClaims.Keys
            .Where(k => k.RunId == runId)
            .Select(k => k.StepKey)
            .ToArray();

        return Task.FromResult(claimed);
    }

    public Task<bool> TryClaimStepAsync(Guid runId, string stepKey)
    {
        var claimed = _stepClaims.TryAdd((runId, stepKey), 1);
        return Task.FromResult(claimed);
    }

    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
    {
        await RecordStepStartAsync(runId, stepKey, stepType, null, null).ConfigureAwait(false);
        await RecordStepCompleteAsync(runId, stepKey, StepStatus.Skipped.ToString(), null, reason).ConfigureAwait(false);
    }

    public Task<string?> GetRunStatusAsync(Guid runId)
    {
        _runs.TryGetValue(runId, out var run);
        return Task.FromResult(run?.Status);
    }

    public Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc)
    {
        _runControls[runId] = new FlowRunControlRecord
        {
            RunId = runId,
            FlowId = flowId,
            TriggerKey = triggerKey,
            IdempotencyKey = idempotencyKey,
            TimeoutAtUtc = timeoutAtUtc
        };

        return Task.CompletedTask;
    }

    public Task<FlowRunControlRecord?> GetRunControlAsync(Guid runId)
    {
        _runControls.TryGetValue(runId, out var control);
        return Task.FromResult<FlowRunControlRecord?>(control);
    }

    public Task<bool> RequestCancelAsync(Guid runId, string? reason)
    {
        if (!_runControls.TryGetValue(runId, out var control))
        {
            control = new FlowRunControlRecord { RunId = runId };
            _runControls[runId] = control;
        }

        if (control.CancelRequested)
        {
            return Task.FromResult(false);
        }

        control.CancelRequested = true;
        control.CancelReason = reason;
        control.CancelRequestedAtUtc = DateTimeOffset.UtcNow;
        return Task.FromResult(true);
    }

    public Task<bool> MarkTimedOutAsync(Guid runId, string? reason)
    {
        if (!_runControls.TryGetValue(runId, out var control))
        {
            control = new FlowRunControlRecord { RunId = runId };
            _runControls[runId] = control;
        }

        if (control.TimedOutAtUtc is not null)
        {
            return Task.FromResult(false);
        }

        control.TimedOutAtUtc = DateTimeOffset.UtcNow;
        control.CancelRequested = true;
        control.CancelReason = reason ?? "Run timed out.";
        control.CancelRequestedAtUtc ??= DateTimeOffset.UtcNow;

        return Task.FromResult(true);
    }

    public Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
    {
        var key = NormalizeIdempotencyKey(flowId, triggerKey, idempotencyKey);
        if (_idempotency.TryGetValue(key, out var runId))
        {
            return Task.FromResult<Guid?>(runId);
        }

        return Task.FromResult<Guid?>(null);
    }

    public Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
    {
        var key = NormalizeIdempotencyKey(flowId, triggerKey, idempotencyKey);
        var added = _idempotency.TryAdd(key, runId);
        return Task.FromResult(added);
    }

    public Task CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        var obsoleteRunIds = _runs.Values
            .Where(r => r.CompletedAt is not null && r.CompletedAt <= cutoffUtc)
            .Select(r => r.Id)
            .ToArray();

        if (obsoleteRunIds.Length == 0)
        {
            return Task.CompletedTask;
        }

        var obsolete = obsoleteRunIds.ToHashSet();

        foreach (var runId in obsoleteRunIds)
        {
            _runs.TryRemove(runId, out _);
            _runControls.TryRemove(runId, out _);
        }

        foreach (var key in _steps.Keys.Where(k => obsolete.Contains(k.RunId)).ToArray())
        {
            _steps.TryRemove(key, out _);
            _stepAttemptCounters.TryRemove(key, out _);
            _stepClaims.TryRemove(key, out _);
        }

        foreach (var key in _stepAttempts.Keys.Where(k => obsolete.Contains(k.RunId)).ToArray())
        {
            _stepAttempts.TryRemove(key, out _);
        }

        foreach (var key in _idempotency.Where(kvp => obsolete.Contains(kvp.Value)).Select(kvp => kvp.Key).ToArray())
        {
            _idempotency.TryRemove(key, out _);
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
            query = query.Where(r => MatchesRunSearch(r, search));

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

        var stepMatch = _steps.Values.Any(s =>
            s.RunId == run.Id
            && (ContainsIgnoreCase(s.StepKey, search)
                || ContainsIgnoreCase(s.ErrorMessage, search)
                || ContainsIgnoreCase(s.OutputJson, search)));

        if (stepMatch)
            return true;

        return _stepAttempts.Values.Any(a =>
            a.RunId == run.Id
            && (ContainsIgnoreCase(a.StepKey, search)
                || ContainsIgnoreCase(a.ErrorMessage, search)
                || ContainsIgnoreCase(a.OutputJson, search)));
    }

    private static bool ContainsIgnoreCase(string? value, string search)
        => !string.IsNullOrEmpty(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static FlowStepRecord CloneStepRecord(FlowStepRecord step) => new()
    {
        RunId = step.RunId,
        StepKey = step.StepKey,
        StepType = step.StepType,
        Status = step.Status,
        InputJson = step.InputJson,
        OutputJson = step.OutputJson,
        ErrorMessage = step.ErrorMessage,
        JobId = step.JobId,
        StartedAt = step.StartedAt,
        CompletedAt = step.CompletedAt,
        AttemptCount = step.AttemptCount
    };

    private static FlowStepAttemptRecord CloneStepAttemptRecord(FlowStepAttemptRecord attempt) => new()
    {
        RunId = attempt.RunId,
        StepKey = attempt.StepKey,
        Attempt = attempt.Attempt,
        StepType = attempt.StepType,
        Status = attempt.Status,
        InputJson = attempt.InputJson,
        OutputJson = attempt.OutputJson,
        ErrorMessage = attempt.ErrorMessage,
        JobId = attempt.JobId,
        StartedAt = attempt.StartedAt,
        CompletedAt = attempt.CompletedAt
    };

    private static FlowStepAttemptRecord CreateSyntheticAttempt(FlowStepRecord step) => new()
    {
        RunId = step.RunId,
        StepKey = step.StepKey,
        Attempt = 1,
        StepType = step.StepType,
        Status = step.Status,
        InputJson = step.InputJson,
        OutputJson = step.OutputJson,
        ErrorMessage = step.ErrorMessage,
        JobId = step.JobId,
        StartedAt = step.StartedAt,
        CompletedAt = step.CompletedAt
    };

    private static StepStatus ParseStepStatus(string status)
    {
        if (Enum.TryParse<StepStatus>(status, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return StepStatus.Failed;
    }

    private static (Guid FlowId, string TriggerKey, string IdempotencyKey) NormalizeIdempotencyKey(Guid flowId, string triggerKey, string idempotencyKey)
    {
        return (flowId, triggerKey.Trim().ToLowerInvariant(), idempotencyKey.Trim().ToLowerInvariant());
    }
}
