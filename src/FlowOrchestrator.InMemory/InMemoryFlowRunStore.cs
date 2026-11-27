using System.Buffers;
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
    private readonly IFlowStore? _flowStore;

    /// <summary>Creates a standalone store; <see cref="GetStatisticsAsync"/> derives its flow count from runs.</summary>
    public InMemoryFlowRunStore()
    {
    }

    /// <summary>Creates a store whose <see cref="DashboardStatistics.TotalFlows"/> counts enabled definitions in <paramref name="flowStore"/>.</summary>
    /// <param name="flowStore">The flow definition store registered alongside this run store.</param>
    public InMemoryFlowRunStore(IFlowStore flowStore)
    {
        _flowStore = flowStore;
    }

    /// <summary>
    /// Clock that stamps claims and judges their age in <see cref="ReleaseOrphanedClaimsAsync"/>; a seam for
    /// tests, the system clock otherwise.
    /// </summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    private readonly ConcurrentDictionary<Guid, FlowRunRecord> _runs = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), FlowStepRecord> _steps = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey, int Attempt), FlowStepAttemptRecord> _stepAttempts = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), int> _stepAttemptCounters = new();
    // Value: when the claim was taken (UTC), read by ReleaseOrphanedClaimsAsync.
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), DateTimeOffset> _stepClaims = new();
    // Value: the runtime job id (null until annotated) and when the row was recorded or last leased.
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), DispatchEntry> _stepDispatches = new();
    private readonly ConcurrentDictionary<Guid, FlowRunControlRecord> _runControls = new();

    // Cached completed tasks for the point-lookup reads, which run on the engine's per-step-completion
    // path: Task.FromResult(bool) allocates a fresh Task on every call.
    private static readonly Task<bool> TrueTask = Task.FromResult(true);
    private static readonly Task<bool> FalseTask = Task.FromResult(false);
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

    // Keys that were written with a Running or Pending status, per run: the index behind
    // HasRunningOrPendingStepAsync. Pruned lazily by that reader — a key whose row is no longer open is
    // removed when it is next looked at — so the set tracks the open steps plus the few closed since the last
    // check, not the run's length. A writer that forgets to add a key can only make the reader answer false
    // too early, which costs the engine a full status read and nothing else.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _openStepsByRun = new();

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

        // The SQL Server / PostgreSQL upserts leave EvaluationTraceJson untouched on a restart (and
        // RecordSkippedStepAsync COALESCEs a null trace into the existing one), so carry it over instead
        // of dropping it with the replaced record.
        _steps.TryGetValue(key, out var previous);
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
            AttemptCount = attempt,
            EvaluationTraceJson = previous?.EvaluationTraceJson
        };

        // Maintain the per-run secondary index so GetStepStatusesAsync and
        // GetRunDetailAsync can enumerate without scanning _steps globally.
        _stepKeysByRun.GetOrAdd(runId, _newRunStringSet).TryAdd(stepKey, 0);
        NoteOpen(runId, stepKey);

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
            if (IsOpenStatus(status))
            {
                NoteOpen(runId, stepKey);
            }
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

    /// <inheritdoc/>
    public Task<bool> TryCompleteRunningStepAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
    {
        if (!_steps.TryGetValue((runId, stepKey), out var step))
        {
            return FalseTask;
        }

        // The record lock serialises every conditional transition of this step (this method and
        // RetryStepAsync), so two concurrent settles can never both observe Running.
        lock (step)
        {
            if (!string.Equals(step.Status, "Running", StringComparison.Ordinal))
            {
                return FalseTask;
            }

            var completedAt = DateTimeOffset.UtcNow;
            step.Status = status;
            step.OutputJson = outputJson;
            step.ErrorMessage = errorMessage;
            step.CompletedAt = completedAt;

            if (_stepAttemptCounters.TryGetValue((runId, stepKey), out var latestAttemptNum)
                && _stepAttempts.TryGetValue((runId, stepKey, latestAttemptNum), out var latestAttempt))
            {
                latestAttempt.Status = status;
                latestAttempt.OutputJson = outputJson;
                latestAttempt.ErrorMessage = errorMessage;
                latestAttempt.CompletedAt = completedAt;
            }

            return TrueTask;
        }
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

    /// <inheritdoc/>
    public Task<bool> CompleteRunIfActiveAsync(Guid runId, string status)
    {
        // Serialise the check-then-set on the run record so two concurrent completers (graph
        // continuation + timeout sweep) can never both observe Running and both transition it.
        if (!_runs.TryGetValue(runId, out var run))
        {
            return Task.FromResult(false);
        }

        lock (run)
        {
            if (!string.Equals(run.Status, "Running", StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            run.Status = status;
            run.CompletedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50)
    {
        IReadOnlyList<FlowRunRecord> result = ApplyRunsFilter(flowId, null, null, null, null, deepSearch: true)
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
        => GetRunsPageAsync(flowId, status, skip, take, search, deepSearch: true);

    public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(
        Guid? flowId,
        string? status,
        int skip,
        int take,
        string? search,
        bool deepSearch,
        DateTimeOffset? startedFrom = null,
        DateTimeOffset? startedTo = null)
    {
        var filtered = ApplyRunsFilter(flowId, status, search, startedFrom, startedTo, deepSearch).OrderByDescending(r => r.StartedAt).ToList();
        var totalCount = filtered.Count;
        IReadOnlyList<FlowRunRecord> runs = filtered.Skip(skip).Take(take).ToList();
        return Task.FromResult((runs, totalCount));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Mirrors the SQL providers' semantics so dashboard behaviour does not depend on the backend: a
    /// run id is an exact lookup, a GUID-shaped text term matches the id by equality, text matches only
    /// the run's own columns unless <see cref="RunSearchQuery.IncludeStepPayloads"/> is set, and the
    /// total is counted only for queries without a text or run-id term.
    /// </remarks>
    public Task<RunSearchPage> SearchRunsAsync(RunSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // A run id is a direct dictionary hit, mirroring the SQL providers' key seek.
        IEnumerable<FlowRunRecord> candidates = query.RunId is { } runId
            ? (_runs.TryGetValue(runId, out var single) ? [single] : [])
            : _runs.Values;

        var text = query.HasText ? query.Text!.Trim() : null;
        Guid? textId = text is not null && Guid.TryParse(text, out var parsed) ? parsed : null;
        // An id prefix matches at any age, like the SQL providers' separate IdText seek.
        var idPrefix = text is not null && textId is null && RunSearchQuery.IsRunIdPrefix(text) ? text : null;

        var matches = new List<FlowRunRecord>();
        // foreach, not .Where(): the predicate captures text/textId/idPrefix/query, so a lambda allocates a
        // closure per search over every stored run; CLAUDE.md hot-path rule (no LINQ with a closure in a loop).
        foreach (var run in candidates)
        {
            if (!MatchesNonWindowFilters(run, query))
                continue;
            var inWindow = MatchesWindow(run, query);
            if (text is null ? inWindow
                : (inWindow && MatchesRunText(run, text, textId, query.IncludeStepPayloads))
                  || (idPrefix is not null && IdStartsWithIgnoreCase(run.Id, idPrefix)))
            {
                matches.Add(run);
            }
        }

        // StartedAt descending, then Id ascending: the same total order RunSearchCursor.Precedes uses, so
        // a cursor taken from one page continues exactly after its last row.
        matches.Sort(static (a, b) => b.StartedAt != a.StartedAt ? b.StartedAt.CompareTo(a.StartedAt) : a.Id.CompareTo(b.Id));

        // The total covers the plain filters, not the cursor: it is the size of the whole list.
        int? total = query.CountsTotal ? matches.Count : null;
        var start = 0;
        if (query.After is { } after)
        {
            // Binary search for the first row after the cursor: the list is sorted in cursor order.
            int lo = 0, hi = matches.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (after.Precedes(matches[mid])) hi = mid;
                else lo = mid + 1;
            }

            start = lo;
        }

        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 0, int.MaxValue - 1);
        start = (int)Math.Min((long)start + skip, matches.Count);
        var rows = matches.GetRange(start, (int)Math.Min((long)take + 1, matches.Count - start));
        var cap = query.EffectiveTotalCountCap;
        if (total is { } exact && cap is { } limit && exact > limit)
            total = limit + 1;   // what a capped COUNT over TOP (cap + 1) reports
        return Task.FromResult(RunSearchPage.FromRows(rows, take, total, cap));
    }

    private static bool MatchesNonWindowFilters(FlowRunRecord run, RunSearchQuery query)
    {
        if (query.FlowId.HasValue && run.FlowId != query.FlowId.Value)
            return false;
        if (!string.IsNullOrWhiteSpace(query.Status) && !string.Equals(run.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool MatchesWindow(FlowRunRecord run, RunSearchQuery query)
    {
        if (query.StartedFrom.HasValue && run.StartedAt < query.StartedFrom.Value)
            return false;
        if (query.StartedTo.HasValue && run.StartedAt > query.StartedTo.Value)
            return false;
        return true;
    }

    /// <summary>
    /// Returns whether the canonical <c>D</c> form of <paramref name="id"/> starts with
    /// <paramref name="prefix"/>, ignoring case, without allocating the formatted id.
    /// </summary>
    private static bool IdStartsWithIgnoreCase(Guid id, string prefix)
    {
        Span<char> buffer = stackalloc char[GuidDFormatLength];
        return id.TryFormat(buffer, out var written, "D")
            && buffer[..written].StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesRunText(FlowRunRecord run, string text, Guid? textId, bool includeStepPayloads)
    {
        var idMatches = textId.HasValue ? run.Id == textId.Value : IdContainsIgnoreCase(run.Id, text);
        if (idMatches
            || ContainsIgnoreCase(run.FlowName, text)
            || ContainsIgnoreCase(run.TriggerKey, text)
            || ContainsIgnoreCase(run.Status, text)
            || ContainsIgnoreCase(run.BackgroundJobId, text))
        {
            return true;
        }

        return includeStepPayloads && MatchesStepRows(run.Id, text);
    }

    /// <inheritdoc/>
    public Task<FlowRunRecord?> GetRunAsync(Guid runId)
        // Header only: no step enumeration, no attempt materialisation. Cloned for the same reason
        // as GetRunDetailAsync — the write path mutates the stored record in place, so a caller
        // holding the live instance sees Status and CompletedAt change under it.
        => Task.FromResult(_runs.TryGetValue(runId, out var run) ? CloneRunRecord(run) : null);

    /// <summary>
    /// Test seam: applies <paramref name="mutate"/> to the stored run header in place.
    /// </summary>
    /// <param name="runId">The run to mutate.</param>
    /// <param name="mutate">Mutation applied to the live record (e.g. back-dating <c>CompletedAt</c>).</param>
    /// <returns><see langword="true"/> when the run exists and was mutated.</returns>
    /// <remarks>
    /// Retention and time-series tests need timestamps the public API cannot produce (a run that
    /// completed 30 days ago). They used to arrange that by mutating the record a read returned,
    /// which only worked while the reads leaked the live instance; they no longer do.
    /// </remarks>
    internal bool TryMutateRunForTests(Guid runId, Action<FlowRunRecord> mutate)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            return false;
        }

        mutate(run);
        return true;
    }

    /// <summary>Returns <see langword="true"/> while the store still holds a run with <paramref name="runId"/>.</summary>
    /// <remarks>Lets <see cref="InMemoryFlowSignalStore"/> follow this store's retention sweep.</remarks>
    internal bool ContainsRun(Guid runId) => _runs.ContainsKey(runId);

    /// <inheritdoc/>
    public Task<FlowStepRecord?> GetStepAsync(Guid runId, string stepKey)
        // Point lookup, cloned for the same reason as every other read: the write path mutates the
        // stored record in place.
        => Task.FromResult(_steps.TryGetValue((runId, stepKey), out var step) ? CloneStepRecord(step) : null);

    public Task<FlowRunRecord?> GetRunDetailAsync(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out var live))
            return Task.FromResult<FlowRunRecord?>(null);

        // Snapshot the header BEFORE enumerating steps, and hand back the copy — never the live
        // record. Returning the live instance made this a torn read: Status/CompletedAt are mutated
        // in place by CompleteRunAsync, so a caller that read run.Status after this method returned
        // could observe a terminal run paired with the step list as it stood at enumeration time.
        // That is the CI-only "Expected 3 steps, Actual 2" failure in
        // HappyPathTests.LinearFlow_runs_to_completion: the poll captured two step rows while the
        // run was still Running, and the engine completed the run in the window before the caller
        // read Status off the same object. Header-first ordering mirrors the SQL backends (run row,
        // then steps, then attempts) and makes the pairing safe in the only direction that matters —
        // the steps are never older than the status. Cloning also stops this read path from writing
        // Steps onto shared state, which leaked a stale step list into later list-view reads.
        var run = CloneRunRecord(live);

        // Enumerate step keys for this run via the secondary index, then
        // direct-look-up each step record. O(steps_in_run) vs the prior
        // O(total_steps_in_history) global scan.
        var stepKeys = _stepKeysByRun.TryGetValue(runId, out var keySet)
            ? keySet.Keys.ToList()
            : new List<string>();

        var steps = stepKeys
            .Select(stepKey => _steps.TryGetValue((runId, stepKey), out var record) ? record : null)
            .Where(record => record is not null)
            .Select(record => CloneStepRecord(record!))
            .ToList();
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

    public async Task<DashboardStatistics> GetStatisticsAsync()
    {
        // "Today" is bucketed in UTC (via UtcDateTime) so the count matches the SQL backends
        // regardless of the offset carried on a stored CompletedAt.
        var today = DateTimeOffset.UtcNow.UtcDateTime.Date;

        // TotalFlows is documented as the number of enabled flow definitions, and the SQL backends
        // now count exactly that. Counting distinct FlowIds over every run grew with run history on
        // a 5 s dashboard poll (#189). Without a paired flow store (a standalone test instance) the
        // old run-derived count is the only information available.
        // Off the step hot path (one call per dashboard poll, bounded by registered flows), so the
        // LINQ predicate is fine here.
        var totalFlows = _flowStore is not null
            ? (await _flowStore.GetAllAsync().ConfigureAwait(false)).Count(flow => flow.IsEnabled)
            : _runs.Values.Select(r => r.FlowId).Distinct().Count();

        var stats = new DashboardStatistics
        {
            TotalFlows = totalFlows,
            ActiveRuns = _runs.Values.Count(r => r.Status == "Running"),
            CompletedToday = _runs.Values.Count(r => r.CompletedAt?.UtcDateTime.Date == today && r.Status == "Succeeded"),
            FailedToday = _runs.Values.Count(r => r.CompletedAt?.UtcDateTime.Date == today && r.Status == "Failed")
        };
        return stats;
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

        foreach (var r in _runs.Values
            .Where(x => x.StartedAt >= anchor && x.StartedAt < until)
            .Where(x => !flowId.HasValue || x.FlowId == flowId.Value))
        {
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

    /// <inheritdoc/>
    public Task<bool> RetryStepAsync(Guid runId, string stepKey)
    {
        if (!_steps.TryGetValue((runId, stepKey), out var step))
        {
            return FalseTask;
        }

        // The record lock serialises the Failed check with the reset (and with TryCompleteRunningStepAsync),
        // so of two concurrent retries only one resets the step; the other changes nothing, and in particular
        // does not delete the winner's claim (RFC 2.0 S4).
        lock (step)
        {
            if (!string.Equals(step.Status, StepStatus.Failed.ToString(), StringComparison.Ordinal))
            {
                return FalseTask;
            }

            step.Status = "Running";
            step.OutputJson = null;
            step.ErrorMessage = null;
            step.CompletedAt = null;
            step.StartedAt = DateTimeOffset.UtcNow;
            NoteOpen(runId, stepKey);

            if (_runs.TryGetValue(runId, out var run))
            {
                run.Status = "Running";
                run.CompletedAt = null;
            }

            // Release any prior execute-time claim so the retried RunStepAsync can re-claim.
            // Mirrored in SqlFlowRunStore + PostgreSqlFlowRunStore. Pre-v1.22 the schedule-time
            // claim was orphaned across retries; the engine now treats the claim as exclusive to
            // a single attempt, so retry must clear it.
            _stepClaims.TryRemove((runId, stepKey), out _);
            if (_claimsByRun.TryGetValue(runId, out var set))
            {
                set.TryRemove(stepKey, out _);
            }

            // Also clear the dispatch ledger entry so the engine's TryRecordDispatchAsync gate
            // accepts the re-dispatch. Without this, retry would silent-skip enqueue.
            _stepDispatches.TryRemove((runId, stepKey), out _);
            if (_dispatchesByRun.TryGetValue(runId, out var dset))
            {
                dset.TryRemove(stepKey, out _);
            }

            return TrueTask;
        }
    }

    public Task ResetCascadeSkippedDependentsAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
    {
        if (stepKeys is null || stepKeys.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var stepKey in stepKeys)
        {
            // Only clear rows that were skipped specifically because an upstream final
            // status didn't match — When-clause skips and manual cancellations carry a
            // different reason and stay intact.
            if (!_steps.TryGetValue((runId, stepKey), out var step))
            {
                continue;
            }
            if (!string.Equals(step.Status, StepStatus.Skipped.ToString(), StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.Equals(step.ErrorMessage, StepSkipReasons.PrerequisitesUnmet, StringComparison.Ordinal))
            {
                continue;
            }

            _steps.TryRemove((runId, stepKey), out _);
            if (_stepKeysByRun.TryGetValue(runId, out var keySet))
            {
                keySet.TryRemove(stepKey, out _);
            }

            _stepClaims.TryRemove((runId, stepKey), out _);
            if (_claimsByRun.TryGetValue(runId, out var claimSet))
            {
                claimSet.TryRemove(stepKey, out _);
            }

            _stepDispatches.TryRemove((runId, stepKey), out _);
            if (_dispatchesByRun.TryGetValue(runId, out var dispatchSet))
            {
                dispatchSet.TryRemove(stepKey, out _);
            }
            // FlowStepAttempts entries are preserved so the dashboard still surfaces the
            // prior cascade-skipped attempt as an audit trail; only the latest-status row
            // is cleared so the planner re-evaluates.
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        var added = _stepDispatches.TryAdd((runId, stepKey), new DispatchEntry(null, Clock.GetUtcNow()));
        if (added)
        {
            _dispatchesByRun.GetOrAdd(runId, _newRunStringSet).TryAdd(stepKey, 0);
        }
        return Task.FromResult(added);
    }

    public Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default)
    {
        _stepDispatches.AddOrUpdate(
            (runId, stepKey),
            static (_, arg) => new DispatchEntry(arg.JobId, arg.Now),
            static (_, entry, arg) => entry with { JobId = arg.JobId },
            (JobId: jobId, Now: Clock.GetUtcNow()));
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

    /// <inheritdoc/>
    public Task<bool> IsStepDispatchedAsync(Guid runId, string stepKey)
        // O(1) on the flat dispatch dictionary. The interface default would copy the run's whole
        // ledger into a HashSet to test one key.
        => _stepDispatches.ContainsKey((runId, stepKey)) ? TrueTask : FalseTask;

    /// <inheritdoc/>
    /// <remarks>
    /// O(dispatch rows of the active runs) per call — walks each <c>Running</c> run's ledger index; the
    /// sweep that calls it runs at most every 30 s, never on the step-completion path. Each row is leased with
    /// a compare-and-swap on its entry, so two concurrent callers never lease the same row.
    /// </remarks>
    public Task<IReadOnlyList<UnsentDispatch>> LeaseUnsentDispatchesAsync(TimeSpan minimumAge, int maxCount, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maxCount <= 0)
        {
            return Task.FromResult<IReadOnlyList<UnsentDispatch>>([]);
        }

        var now = Clock.GetUtcNow();
        var cutoff = now - (minimumAge < TimeSpan.Zero ? TimeSpan.Zero : minimumAge);
        List<UnsentDispatch>? leased = null;

        foreach (var run in _runs.Values)
        {
            if (run.Status != "Running" || !_dispatchesByRun.TryGetValue(run.Id, out var dispatchSet))
            {
                continue;
            }

            foreach (var stepKey in dispatchSet.Keys)
            {
                if (leased is not null && leased.Count >= maxCount)
                {
                    return Task.FromResult<IReadOnlyList<UnsentDispatch>>(leased);
                }

                var key = (run.Id, stepKey);
                if (!_stepDispatches.TryGetValue(key, out var entry)
                    || entry.JobId is not null
                    || entry.At > cutoff
                    || _stepClaims.ContainsKey(key)
                    || (_steps.TryGetValue(key, out var step) && ParseStepStatus(step.Status) != StepStatus.Pending)
                    || !_stepDispatches.TryUpdate(key, entry with { At = now }, entry))
                {
                    continue;
                }

                (leased ??= []).Add(new UnsentDispatch(run.Id, run.FlowId, stepKey));
            }
        }

        return Task.FromResult<IReadOnlyList<UnsentDispatch>>(leased ?? []);
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

        var map = stepKeys.Keys
            .Select(stepKey => (stepKey, record: _steps.TryGetValue((runId, stepKey), out var r) ? r : null))
            .Where(pair => pair.record is not null)
            .ToDictionary(pair => pair.stepKey, pair => ParseStepStatus(pair.record!.Status), StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(map);
    }

    /// <inheritdoc/>
    /// <remarks>O(keys): one dictionary probe per key, independent of the run's length.</remarks>
    public Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
    {
        var map = new Dictionary<string, StepStatus>(stepKeys.Count, StringComparer.Ordinal);
        foreach (var stepKey in stepKeys)
        {
            if (_steps.TryGetValue((runId, stepKey), out var record))
            {
                map[stepKey] = ParseStepStatus(record.Status);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(map);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// O(open steps + steps closed since the last call) through the per-run open-step index, which this
    /// reader prunes as it goes; the answer is decided on each row's current status, never on the index alone.
    /// </remarks>
    public Task<bool> HasRunningOrPendingStepAsync(Guid runId)
    {
        if (!_openStepsByRun.TryGetValue(runId, out var open))
        {
            return FalseTask;
        }

        foreach (var pair in open)
        {
            var stepKey = pair.Key;
            if (_steps.TryGetValue((runId, stepKey), out var record) && IsOpenStatus(record.Status))
            {
                return TrueTask;
            }

            // Closed (or purged) since it was noted. A writer re-opening the key concurrently may lose its
            // entry to this removal; that only makes a later call answer false, which the engine treats as
            // "read the full map", never as "close the run".
            open.TryRemove(stepKey, out _);
        }

        return FalseTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One pass over the run's step keys through the per-run index, parsed as spans, tallying terminal body
    /// rows per iteration in a pooled array — no allocation proportional to the run. The in-memory counterpart
    /// of the SQL backends' range read over the loop's rows, and like it O(rows of the run) in CPU: the
    /// remaining linear term of the loop barrier until the store keeps per-loop counters (RFC 2.0 §4.7).
    /// </remarks>
    public Task<int> CountSettledIterationsAsync(Guid runId, string loopStepKey, int iterations, IReadOnlyCollection<string> bodyStepKeys)
    {
        if (iterations <= 0 || bodyStepKeys.Count == 0 || !_stepKeysByRun.TryGetValue(runId, out var stepKeys))
        {
            return Task.FromResult(0);
        }

        var body = new HashSet<string>(bodyStepKeys, StringComparer.Ordinal);
        var bodyLookup = body.GetAlternateLookup<ReadOnlySpan<char>>();
        var tally = ArrayPool<int>.Shared.Rent(iterations);
        try
        {
            Array.Clear(tally, 0, iterations);

            // The ConcurrentDictionary enumerator walks the live buckets; .Keys would copy every key first.
            foreach (var pair in stepKeys)
            {
                var stepKey = pair.Key;
                if (stepKey.Length <= loopStepKey.Length + 1
                    || stepKey[loopStepKey.Length] != '.'
                    || !stepKey.StartsWith(loopStepKey, StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = stepKey.AsSpan(loopStepKey.Length + 1);
                var dot = rest.IndexOf('.');
                if (dot <= 0
                    || !TryParseCanonicalIndex(rest[..dot], out var index)
                    || index >= iterations
                    || !bodyLookup.Contains(rest[(dot + 1)..])
                    || !_steps.TryGetValue((runId, stepKey), out var record)
                    || !IsTerminalStatus(record.Status))
                {
                    continue;
                }

                tally[index]++;
            }

            var settled = 0;
            for (var index = 0; index < iterations; index++)
            {
                if (tally[index] == body.Count)
                {
                    settled++;
                }
            }

            return Task.FromResult(settled);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(tally);
        }
    }

    public Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId)
    {
        // O(claimed_steps_in_run) via the secondary index.
        IReadOnlyCollection<string> claimed = _claimsByRun.TryGetValue(runId, out var set)
            ? set.Keys.ToArray()
            : Array.Empty<string>();
        return Task.FromResult(claimed);
    }

    /// <inheritdoc/>
    public Task<bool> IsStepClaimedAsync(Guid runId, string stepKey)
        // O(1) on the flat claim dictionary. The interface default would materialise every claimed
        // key in the run — claims are never released on terminal statuses — to test one key.
        => Task.FromResult(_stepClaims.ContainsKey((runId, stepKey)));

    public Task<bool> TryClaimStepAsync(Guid runId, string stepKey)
    {
        var claimed = _stepClaims.TryAdd((runId, stepKey), Clock.GetUtcNow());
        if (claimed)
        {
            _claimsByRun.GetOrAdd(runId, _newRunStringSet).TryAdd(stepKey, 0);
        }
        return Task.FromResult(claimed);
    }

    public Task ReleaseStepClaimAsync(Guid runId, string stepKey)
    {
        _stepClaims.TryRemove((runId, stepKey), out _);
        if (_claimsByRun.TryGetValue(runId, out var set))
        {
            set.TryRemove(stepKey, out _);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// O(claims in the run) via the per-run claim index. A claim is removed only if its timestamp is still
    /// the one that was judged old enough, so a claim re-taken in between is kept.
    /// </remarks>
    public Task<IReadOnlyList<string>> ReleaseOrphanedClaimsAsync(Guid runId, TimeSpan minimumAge)
    {
        if (!_claimsByRun.TryGetValue(runId, out var claimSet))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var cutoff = Clock.GetUtcNow() - (minimumAge < TimeSpan.Zero ? TimeSpan.Zero : minimumAge);
        List<string>? released = null;
        foreach (var stepKey in claimSet.Keys)
        {
            var key = (runId, stepKey);
            if (_steps.ContainsKey(key)
                || !_stepClaims.TryGetValue(key, out var claimedAt)
                || claimedAt > cutoff
                || !_stepClaims.TryRemove(new KeyValuePair<(Guid RunId, string StepKey), DateTimeOffset>(key, claimedAt)))
            {
                continue;
            }

            claimSet.TryRemove(stepKey, out _);
            if (_stepDispatches.TryRemove(key, out _) && _dispatchesByRun.TryGetValue(runId, out var dispatchSet))
            {
                dispatchSet.TryRemove(stepKey, out _);
            }

            (released ??= []).Add(stepKey);
        }

        return Task.FromResult<IReadOnlyList<string>>(released is null ? [] : released);
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

    /// <inheritdoc/>
    /// <remarks>
    /// An upsert like the SQL Server MERGE / PostgreSQL <c>ON CONFLICT</c>: configuring an existing record
    /// rewrites the flow, trigger, idempotency key and deadline but keeps its cancel and timeout latches,
    /// so a cancel recorded before a late configure is not silently dropped.
    /// </remarks>
    public Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc)
    {
        var storedKey = IdempotencyKeyStorage.ToStorageForm(idempotencyKey);
        _runControls.AddOrUpdate(
            runId,
            static (id, args) => new FlowRunControlRecord
            {
                RunId = id,
                FlowId = args.flowId,
                TriggerKey = args.triggerKey,
                IdempotencyKey = args.storedKey,
                TimeoutAtUtc = args.timeoutAtUtc
            },
            static (_, existing, args) =>
            {
                existing.FlowId = args.flowId;
                existing.TriggerKey = args.triggerKey;
                existing.IdempotencyKey = args.storedKey;
                existing.TimeoutAtUtc = args.timeoutAtUtc;
                return existing;
            },
            (flowId, triggerKey, storedKey, timeoutAtUtc));

        return Task.CompletedTask;
    }

    public Task<FlowRunControlRecord?> GetRunControlAsync(Guid runId)
    {
        _runControls.TryGetValue(runId, out var control);
        return Task.FromResult<FlowRunControlRecord?>(control);
    }

    public Task<bool> RequestCancelAsync(Guid runId, string? reason)
    {
        // Mirror the SQL backends' UPDATE ... WHERE RunId=@RunId semantics: return false when
        // no control record exists (do NOT fabricate a phantom record). The IFlowRunControlStore
        // contract specifies true only when an existing record was found AND updated.
        if (!_runControls.TryGetValue(runId, out var control))
        {
            return Task.FromResult(false);
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
        // Mirror the SQL backends: return false when no control record exists rather than
        // creating one. See RequestCancelAsync for the contract rationale.
        if (!_runControls.TryGetValue(runId, out var control))
        {
            return Task.FromResult(false);
        }

        // A user cancel that landed first wins: latching the timeout over it would relabel the run
        // TimedOut and let a later retry's ExtendDeadlineAsync clear the user's cancel. Same guard as
        // the SQL backends' "AND CancelRequested = 0".
        if (control.TimedOutAtUtc is not null || control.CancelRequested)
        {
            return Task.FromResult(false);
        }

        control.TimedOutAtUtc = DateTimeOffset.UtcNow;
        control.CancelRequested = true;
        control.CancelReason = reason ?? control.CancelReason ?? "Run timed out.";
        control.CancelRequestedAtUtc ??= DateTimeOffset.UtcNow;

        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> ExtendDeadlineAsync(Guid runId, DateTimeOffset? newTimeoutAtUtc)
    {
        // Mirror the SQL backends: return false when no control record exists rather than
        // creating one. See RequestCancelAsync for the contract rationale.
        if (!_runControls.TryGetValue(runId, out var control))
        {
            return Task.FromResult(false);
        }

        control.TimeoutAtUtc = newTimeoutAtUtc;

        // Only un-latch a timeout-induced termination. A genuine user cancellation
        // (CancelRequested set while TimedOutAtUtc was null) must survive a retry.
        if (control.TimedOutAtUtc is not null)
        {
            control.TimedOutAtUtc = null;
            control.CancelRequested = false;
            control.CancelReason = null;
            control.CancelRequestedAtUtc = null;
        }

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

    /// <inheritdoc/>
    public Task<bool> ReleaseIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
    {
        var key = NormalizeIdempotencyKey(flowId, triggerKey, idempotencyKey);

        // Conditional on the value: a registration that already points at another run is not ours to drop.
        var removed = _idempotency.TryRemove(new KeyValuePair<(Guid FlowId, string TriggerKey, string IdempotencyKey), Guid>(key, runId));
        return Task.FromResult(removed);
    }

    public Task CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        // Strictly-less-than the cutoff, matching both SQL backends (`completed_at < @CutoffUtc`).
        // A run completed exactly AT the cutoff is retained.
        var obsoleteRunIds = _runs.Values
            .Where(r => r.CompletedAt is not null && r.CompletedAt < cutoffUtc)
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

            // Drop the per-run secondary indexes too. They are keyed by run id, so a purged run
            // leaves a whole nested dictionary behind if they are not cleared here — and because
            // the hot-path readers (GetStepStatusesAsync / GetClaimedStepKeysAsync /
            // GetDispatchedStepKeysAsync) read THESE rather than the flat dictionaries, a purged
            // run would otherwise keep reporting steps, claims and dispatches that no longer exist.
            _stepKeysByRun.TryRemove(runId, out _);
            _claimsByRun.TryRemove(runId, out _);
            _dispatchesByRun.TryRemove(runId, out _);
            _openStepsByRun.TryRemove(runId, out _);
        }

        foreach (var key in _steps.Keys.Where(k => obsolete.Contains(k.RunId)).ToArray())
        {
            _steps.TryRemove(key, out _);
            _stepAttemptCounters.TryRemove(key, out _);
            _stepClaims.TryRemove(key, out _);
        }

        // The dispatch ledger is keyed independently of _steps: TryRecordDispatchAsync can record a
        // key that never produced a step row (dispatched, then the run was cancelled before the
        // worker picked it up), so it cannot be folded into the loop above without leaking exactly
        // those entries. In a long-lived host this is the entry that grows without bound, since
        // every dispatched step in every completed run contributes one.
        foreach (var key in _stepDispatches.Keys.Where(k => obsolete.Contains(k.RunId)).ToArray())
        {
            _stepDispatches.TryRemove(key, out _);
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

    private IEnumerable<FlowRunRecord> ApplyRunsFilter(Guid? flowId, string? status, string? search, DateTimeOffset? startedFrom, DateTimeOffset? startedTo, bool deepSearch)
    {
        var query = _runs.Values.AsEnumerable();

        if (flowId.HasValue)
            query = query.Where(r => r.FlowId == flowId.Value);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase));

        if (startedFrom.HasValue)
            query = query.Where(r => r.StartedAt >= startedFrom.Value);

        if (startedTo.HasValue)
            query = query.Where(r => r.StartedAt <= startedTo.Value);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(r => MatchesRunSearch(r, search, deepSearch));

        return query;
    }

    private bool MatchesRunSearch(FlowRunRecord run, string search, bool deepSearch)
    {
        if (IdContainsIgnoreCase(run.Id, search)
            || ContainsIgnoreCase(run.FlowName, search)
            || ContainsIgnoreCase(run.TriggerKey, search)
            || ContainsIgnoreCase(run.Status, search)
            || ContainsIgnoreCase(run.BackgroundJobId, search))
        {
            return true;
        }

        return deepSearch && MatchesStepRows(run.Id, search);
    }

    private bool MatchesStepRows(Guid runId, string search)
    {
        // Scans this run's current step rows (incl. OutputJson). Enumerate via the per-run step-key
        // index (O(steps_in_run)) and direct-look-up each step, instead of scanning the global _steps
        // dictionary (O(total_steps) per run — quadratic over run history). Attempt history is
        // intentionally not searched — it duplicates the current row.
        if (!_stepKeysByRun.TryGetValue(runId, out var stepKeys))
            return false;

        foreach (var stepKey in stepKeys.Keys)
        {
            if (_steps.TryGetValue((runId, stepKey), out var s)
                && (ContainsIgnoreCase(s.StepKey, search)
                    || ContainsIgnoreCase(s.ErrorMessage, search)
                    || ContainsIgnoreCase(s.OutputJson, search)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIgnoreCase(string? value, string search)
        => !string.IsNullOrEmpty(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>Length of a <see cref="Guid"/> in its default <c>"D"</c> format.</summary>
    private const int GuidDFormatLength = 36;

    /// <summary>
    /// Returns what <c>ContainsIgnoreCase(id.ToString(), search)</c> returns, without allocating the
    /// 36-character string: the id is formatted into a stack buffer and searched as a span.
    /// </summary>
    /// <remarks>
    /// Runs once per candidate run of every text search, so the string it replaces was ~96 bytes per
    /// run — about 1 MB per search at 10k runs (#189). A term longer than the formatted id cannot
    /// occur in it, which also short-circuits the common case of a long free-text search.
    /// </remarks>
    private static bool IdContainsIgnoreCase(Guid id, string search)
    {
        if (search.Length > GuidDFormatLength)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[GuidDFormatLength];
        if (!id.TryFormat(buffer, out var written))
        {
            return false;
        }

        ReadOnlySpan<char> formatted = buffer[..written];
        return formatted.Contains(search.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies a run header so readers get an immutable point-in-time snapshot instead of the live
    /// record the write path mutates in place.
    /// </summary>
    private static FlowRunRecord CloneRunRecord(FlowRunRecord run) => new()
    {
        Id = run.Id,
        FlowId = run.FlowId,
        FlowName = run.FlowName,
        Status = run.Status,
        TriggerKey = run.TriggerKey,
        TriggerDataJson = run.TriggerDataJson,
        TriggerHeaders = run.TriggerHeaders,
        BackgroundJobId = run.BackgroundJobId,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        SourceRunId = run.SourceRunId
    };

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

    private void NoteOpen(Guid runId, string stepKey)
        => _openStepsByRun.GetOrAdd(runId, _newRunStringSet).TryAdd(stepKey, 0);

    // Both decide on ParseStepStatus, the same parse GetStepStatusesAsync applies, so these readers can never
    // disagree with the status map the engine classifies from.
    private static bool IsOpenStatus(string status)
        => ParseStepStatus(status) is StepStatus.Running or StepStatus.Pending;

    private static bool IsTerminalStatus(string status)
        => ParseStepStatus(status) is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped;

    /// <summary>Parses a decimal iteration index the way the engine writes it: no sign, no padding, no leading zero.</summary>
    private static bool TryParseCanonicalIndex(ReadOnlySpan<char> segment, out int index)
    {
        index = 0;
        if (segment.IsEmpty || segment.Length > 9 || (segment[0] == '0' && segment.Length > 1))
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            index = (index * 10) + (c - '0');
        }

        return true;
    }

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
        return (flowId, triggerKey.Trim().ToLowerInvariant(), IdempotencyKeyStorage.Normalize(idempotencyKey));
    }

    /// <summary>One dispatch-ledger row: the runtime job id once annotated, and when it was recorded or last leased.</summary>
    /// <param name="JobId">The runtime job/message id; <see langword="null"/> until the hand-off returns.</param>
    /// <param name="At">When the row was recorded, re-stamped when <see cref="LeaseUnsentDispatchesAsync"/> leases it.</param>
    private readonly record struct DispatchEntry(string? JobId, DateTimeOffset At);
}
