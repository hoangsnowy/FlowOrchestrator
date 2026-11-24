using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// The default <see cref="IFlowRunStore.SearchRunsAsync"/> honours the 2.0 keyset cursor and count cap
/// for a third-party provider that only implements the legacy offset overload.
/// </summary>
public sealed class RunSearchDefaultCursorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pages_chained_by_cursor_visit_every_run_once_even_with_tied_start_times()
    {
        // Arrange — 7 runs, three of them sharing one start time across the page boundary.
        var runs = new List<FlowRunRecord>();
        for (var i = 0; i < 4; i++) runs.Add(new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = Start.AddMinutes(-i) });
        for (var i = 0; i < 3; i++) runs.Add(new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = Start.AddMinutes(-10) });
        IFlowRunStore store = new OffsetOnlyStore(runs);

        // Act
        var seen = new List<Guid>();
        RunSearchCursor? after = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await store.SearchRunsAsync(new RunSearchQuery { Take = 3, After = after });
            seen.AddRange(page.Runs.Select(r => r.Id));
            if (!page.HasMore) break;
            after = page.NextCursor;
        }

        // Assert
        Assert.Equal(runs.Count, seen.Count);
        Assert.Equal(runs.Select(r => r.Id).OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Fact]
    public async Task Total_with_a_cursor_is_the_size_of_the_whole_list()
    {
        // Arrange
        var runs = Enumerable.Range(0, 5).Select(i => new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = Start.AddMinutes(-i) }).ToList();
        IFlowRunStore store = new OffsetOnlyStore(runs);
        var first = await store.SearchRunsAsync(new RunSearchQuery { Take = 2, IncludeTotal = true });

        // Act
        var second = await store.SearchRunsAsync(new RunSearchQuery { Take = 2, IncludeTotal = true, After = first.NextCursor });

        // Assert
        Assert.Equal(5, first.TotalCount);
        Assert.Equal(5, second.TotalCount);
        Assert.Equal(runs[2].Id, second.Runs[0].Id);
    }

    [Fact]
    public async Task Count_above_the_cap_is_reported_as_a_lower_bound()
    {
        // Arrange
        var runs = Enumerable.Range(0, 12).Select(i => new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = Start.AddMinutes(-i) }).ToList();
        IFlowRunStore store = new OffsetOnlyStore(runs);

        // Act
        var page = await store.SearchRunsAsync(new RunSearchQuery { Take = 2, IncludeTotal = true, TotalCountCap = 10 });

        // Assert
        Assert.Equal(10, page.TotalCount);
        Assert.True(page.TotalIsLowerBound);
    }

    /// <summary>A provider that implements only the legacy offset overload, newest first, honouring the time bounds.</summary>
    private sealed class OffsetOnlyStore(List<FlowRunRecord> runs) : IFlowRunStore
    {
        public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId, string? status, int skip, int take, string? search, bool deepSearch, DateTimeOffset? startedFrom = null, DateTimeOffset? startedTo = null)
        {
            var filtered = runs
                .Where(r => (startedFrom is null || r.StartedAt >= startedFrom) && (startedTo is null || r.StartedAt <= startedTo))
                .OrderByDescending(r => r.StartedAt)
                .ToList();
            IReadOnlyList<FlowRunRecord> page = filtered.Skip(skip).Take(take).ToList();
            return Task.FromResult((page, filtered.Count));
        }

        public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId = null, string? status = null, int skip = 0, int take = 50, string? search = null)
            => throw new NotSupportedException();
        public Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId, Guid? sourceRunId = null) => throw new NotSupportedException();
        public Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId) => throw new NotSupportedException();
        public Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage) => throw new NotSupportedException();
        public Task<bool> TryCompleteRunningStepAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage) => throw new NotSupportedException();
        public Task CompleteRunAsync(Guid runId, string status) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50) => throw new NotSupportedException();
        public Task<FlowRunRecord?> GetRunDetailAsync(Guid runId) => throw new NotSupportedException();
        public Task<DashboardStatistics> GetStatisticsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync() => throw new NotSupportedException();
        public Task<bool> RetryStepAsync(Guid runId, string stepKey) => throw new NotSupportedException();
        public Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReleaseDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> GetDispatchedStepKeysAsync(Guid runId) => throw new NotSupportedException();
    }
}
