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
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), string?> _stepDispatches = new();
    private readonly ConcurrentDictionary<Guid, FlowRunControlRecord> _runControls = new();
    private readonly ConcurrentDictionary<(Guid FlowId, string TriggerKey, string IdempotencyKey), Guid> _idempotency = new();

    // ── Secondary per-run indexes ─────────────────────────────────────────────
    //
    // Engine hot-path methods (GetStepStatusesAsync / GetClaimedStepKeysAsync /
    // GetDispatchedStepKeysAsync / GetRunDetailAsync) used to scan the global
    // ConcurrentDictionaries above with a `Where(k => k.RunId == runId)` filter.
    // That's O(total_steps_in_history) per call — fine for the unit-test scale,
    // but throughput degrades linearly as run history grows in long-lived
    // processes. The engine calls these methods 2x per step completion, so the
    // cost compounds.
    //
    // The secondary indexes below are (Guid runId) → set of step keys for that
    // run. They are maintained alongside the flat dictionaries on every write,
    // and read by the hot-path methods to enumerate just one run's keys in
    // O(steps_in_run). Concurrency: each per-run inner dictionary is its own
    // ConcurrentDictionary, so concurrent step starts / claims / dispatches on
    // the SAME run are race-safe; eventual consistency between the flat dict
    // and the index is acceptable because the engine reads after writing on
    // the same logical thread.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _stepKeysByRun = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _claimsByRun = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _dispatchesByRun = new();

    private static readonly Func<Guid, ConcurrentDictionary<string, byte>> _newRunStringSet =
        static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

    public Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId, Guid? sourceRunId = null)
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
            StartedAt = DateTimeOffset.UtcNow,
            SourceRunId = sourceRunId
        };
        _runs[runId] = record;
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<FlowRunRecord>> GetDerivedRunsAsync(Guid sourceRunId)
    {
        IReadOnlyList<FlowRunRecord> result = _runs.Values
            .Where(r => r.SourceRunId == sourceRunId)
            .OrderByDescending(r => r.StartedAt)
            .ToList();
        return Task.FromResult(result);
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

        // Maintain the per-run secondary index so GetStepStatusesAsync and
        // GetRunDetailAsync can enumerate without scanning _steps globally.
        _stepKeysByRun.GetOrAdd(runId, _newRunStringSet).TryAdd(stepKey, 0);

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

        // The latest attempt number is already tracked in _stepAttemptCounters
        // by RecordStepStartAsync — no need to scan _stepAttempts.Values to
        // find it. O(1) lookup vs O(total_attempts_in_history).
        if (_stepAttemptCounters.TryGetValue((runId, stepKey), out var latestAttemptNum)
            && _stepAttempts.TryGetValue((runId, stepKey, latestAttemptNum), out var latestAttempt))
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

        // Enumerate step keys for this run via the secondary index, then
        // direct-look-up each step record. O(steps_in_run) vs the prior
        // O(total_steps_in_history) global scan.
        var stepKeys = _stepKeysByRun.TryGetValue(runId, out var keySet)
            ? keySet.Keys.ToList()
            : new List<string>();

        var steps = new List<FlowStepRecord>(stepKeys.Count);
        foreach (var stepKey in stepKeys)
        {
            if (_steps.TryGetValue((runId, stepKey), out var record))
            {
                steps.Add(CloneStepRecord(record));
            }
        }
        steps.Sort(static (a, b) => a.StartedAt.CompareTo(b.StartedAt));

        // Attempts: use _stepAttemptCounters for the per-step attempt count,
        // then iterate attempts 1..count for each step. Replaces the prior
        // global scan + GroupBy.
        foreach (var step in steps)
        {
            var attempts = new List<FlowStepAttemptRecord>();
            if (_stepAttemptCounters.TryGetValue((runId, step.StepKey), out var maxAttempt))
            {
                for (var i = 1; i <= maxAttempt; i++)
                {
                    if (_stepAttempts.TryGetValue((runId, step.StepKey, i), out var attempt))
                    {
                        attempts.Add(CloneStepAttemptRecord(attempt));
                    }
                }
            }

            if (attempts.Count == 0)
            {
                attempts.Add(CreateSyntheticAttempt(step));
            }

            step.Attempts = attempts;
            step.AttemptCount = attempts.Count;
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

    public Task<IReadOnlyList<RunTimeseriesBucket>> GetRunTimeseriesAsync(
        RunTimeseriesGranularity granularity,
        DateTimeOffset since,
        DateTimeOffset until,
        Guid? flowId = null)
    {
        var bucketSize = granularity == RunTimeseriesGranularity.Hour ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        // Floor `since` to the bucket boundary so the rendered axis is aligned.
        var anchor = granularity == RunTimeseriesGranularity.Hour
            ? new DateTimeOffset(since.UtcDateTime.Year, since.UtcDateTime.Month, since.UtcDateTime.Day, since.UtcDateTime.Hour, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(since.UtcDateTime.Date, TimeSpan.Zero);
        if (anchor > since) anchor -= bucketSize;

        var totalBuckets = (int)Math.Ceiling((until - anchor).TotalMilliseconds / bucketSize.TotalMilliseconds);
        if (totalBuckets <= 0) return Task.FromResult<IReadOnlyList<RunTimeseriesBucket>>(Array.Empty<RunTimeseriesBucket>());
        if (totalBuckets > 10_000) totalBuckets = 10_000; // hard cap, defensive

        // Pre-allocate empty buckets so gaps in run history still appear in the timeline.
        var buckets = new RunTimeseriesBucket[totalBuckets];
        var durations = new List<double>[totalBuckets];
        for (int i = 0; i < totalBuckets; i++)
        {
            buckets[i] = new RunTimeseriesBucket { Timestamp = anchor + bucketSize * i };
            durations[i] = new List<double>(capacity: 4);
        }

        foreach (var r in _runs.Values)
        {
            if (r.StartedAt < anchor || r.StartedAt >= until) continue;
            if (flowId.HasValue && r.FlowId != flowId.Value) continue;

            var idx = (int)((r.StartedAt - anchor).TotalMilliseconds / bucketSize.TotalMilliseconds);
            if (idx < 0 || idx >= totalBuckets) continue;

            var b = buckets[idx];
            b.Total++;
            switch (r.Status)
            {
                case "Succeeded": b.Succeeded++; break;
                case "Failed": b.Failed++; break;
                case "Cancelled": b.Cancelled++; break;
                default: b.Running++; break;
            }
            if (r.CompletedAt.HasValue && r.Status != "Running")
            {
                durations[idx].Add((r.CompletedAt.Value - r.StartedAt).TotalMilliseconds);
            }
        }

        for (int i = 0; i < totalBuckets; i++)
        {
            var d = durations[i];
            if (d.Count == 0) continue;
            d.Sort();
            buckets[i].P50DurationMs = Percentile(d, 0.50);
            buckets[i].P95DurationMs = Percentile(d, 0.95);
        }

        return Task.FromResult<IReadOnlyList<RunTimeseriesBucket>>(buckets);

        static double Percentile(List<double> sorted, double p)
        {
            // Linear-interpolation percentile (NIST type 7) — same as numpy default.
            if (sorted.Count == 1) return sorted[0];
            var rank = (sorted.Count - 1) * p;
            var lo = (int)Math.Floor(rank);
            var hi = (int)Math.Ceiling(rank);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
        }
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

    public Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        var added = _stepDispatches.TryAdd((runId, stepKey), null);
        if (added)
        {
            _dispatchesByRun.GetOrAdd(runId, _newRunStringSet).TryAdd(stepKey, 0);
        }
        return Task.FromResult(added);
    }

    public Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default)
    {
        _stepDispatches[(runId, stepKey)] = jobId;
        // Annotate is called only after a successful TryRecordDispatch so the
        // per-run index already contains this step key; no maintenance needed.
        return Task.CompletedTask;
    }

    public Task ReleaseDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        if (_stepDispatches.TryRemove((runId, stepKey), out _)
            && _dispatchesByRun.TryGetValue(runId, out var set))
        {
            set.TryRemove(stepKey, out _);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetDispatchedStepKeysAsync(Guid runId)
    {
        // O(steps_in_run) via the secondary index instead of O(total_steps_in_history).
        IReadOnlySet<string> keys = _dispatchesByRun.TryGetValue(runId, out var set)
            ? set.Keys.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return Task.FromResult(keys);
    }

    public Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId)
    {
        // O(steps_in_run) via the secondary index. Each step key is unique per
        // (runId, stepKey) so the prior GroupBy + OrderByDescending(StartedAt)
        // pattern collapses to a direct dictionary lookup.
        if (!_stepKeysByRun.TryGetValue(runId, out var stepKeys))
        {
            return Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(
                new Dictionary<string, StepStatus>(StringComparer.Ordinal));
        }

        var map = new Dictionary<string, StepStatus>(stepKeys.Count, StringComparer.Ordinal);
        foreach (var stepKey in stepKeys.Keys)
        {
            if (_steps.TryGetValue((runId, stepKey), out var record))
            {
                map[stepKey] = ParseStepStatus(record.Status);
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(map);
    }

    public Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId)
    {
        // O(claimed_steps_in_run) via the secondary index.
        IReadOnlyCollection<string> claimed = _claimsByRun.TryGetValue(runId, out var set)
            ? set.Keys.ToArray()
            : Array.Empty<string>();
        return Task.FromResult(claimed);
    }

    public Task<bool> TryClaimStepAsync(Guid runId, string stepKey)
    {
        var claimed = _stepClaims.TryAdd((runId, stepKey), 1);
        if (claimed)
        {
            _claimsByRun.GetOrAdd(runId, _newRunStringSet).TryAdd(stepKey, 0);
        }
        return Task.FromResult(claimed);
    }

    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
    {
        await RecordSkippedStepAsync(runId, stepKey, stepType, reason, evaluationTraceJson: null).ConfigureAwait(false);
    }

    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason, string? evaluationTraceJson)
    {
        await RecordStepStartAsync(runId, stepKey, stepType, null, null).ConfigureAwait(false);
        await RecordStepCompleteAsync(runId, stepKey, StepStatus.Skipped.ToString(), null, reason).ConfigureAwait(false);

        if (string.IsNullOrEmpty(evaluationTraceJson))
        {
            return;
        }

        if (_steps.TryGetValue((runId, stepKey), out var step))
        {
            step.EvaluationTraceJson = evaluationTraceJson;
        }

        // O(1) latest-attempt lookup via the per-(runId, stepKey) counter.
        if (_stepAttemptCounters.TryGetValue((runId, stepKey), out var latestAttemptNum)
            && _stepAttempts.TryGetValue((runId, stepKey, latestAttemptNum), out var latestAttempt))
        {
            latestAttempt.EvaluationTraceJson = evaluationTraceJson;
        }
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
        AttemptCount = step.AttemptCount,
        EvaluationTraceJson = step.EvaluationTraceJson
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
        CompletedAt = attempt.CompletedAt,
        EvaluationTraceJson = attempt.EvaluationTraceJson
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
        CompletedAt = step.CompletedAt,
        EvaluationTraceJson = step.EvaluationTraceJson
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
