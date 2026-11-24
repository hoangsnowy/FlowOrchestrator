using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// The default <see cref="IFlowRunStore.SearchRunsAsync"/> must page by cursor without duplicating or
/// dropping runs when the legacy offset overload returns runs that share a start time in an order other
/// than the cursor's <c>(StartedAt desc, Id asc)</c> — the interface itself says that order is undefined.
/// </summary>
public sealed class RunSearchDefaultCursorTieOrderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_page_cursor_continues_in_cursor_order_when_tied_runs_come_back_in_a_different_order()
    {
        // Arrange — four runs started in the same tick (bursty triggers on a coarse clock), which the
        // provider returns newest-first with ties in descending id order: a legal "undefined" order.
        var ids = new[]
        {
            Guid.Parse("00000000-0000-0000-0000-000000000004"),
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
        };
        var runs = ids.Select(id => new FlowRunRecord { Id = id, StartedAt = Start }).ToList();
        IFlowRunStore store = new TieOrderStore(runs);

        // Act
        var seen = new List<Guid>();
        RunSearchCursor? after = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await store.SearchRunsAsync(new RunSearchQuery { Take = 2, After = after });
            seen.AddRange(page.Runs.Select(r => r.Id));
            if (!page.HasMore) break;
            after = page.NextCursor;
        }

        // Assert — every run exactly once.
        Assert.Equal(ids.OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Theory]
    [InlineData(20, 1, 3)]     // one tie group far larger than the old 8-row slack
    [InlineData(150, 1, 7)]    // a tie group larger than the first 64-row group read
    [InlineData(60, 4, 5)]     // several tie groups, page boundaries inside groups
    [InlineData(37, 37, 4)]    // no ties at all
    [InlineData(25, 2, 1)]     // one row per page
    public async Task Paging_visits_every_run_once_in_cursor_order_whatever_the_number_of_ties(int count, int distinctTimes, int take)
    {
        // Arrange
        var runs = Enumerable.Range(1, count)
            .Select(i => new FlowRunRecord { Id = IdOf(i), StartedAt = Start.AddSeconds(-(i % distinctTimes)) })
            .ToList();
        IFlowRunStore store = new TieOrderStore(runs);

        // Act
        var seen = await ReadAllPagesAsync(store, take);

        // Assert
        var expected = runs.OrderByDescending(r => r.StartedAt).ThenBy(r => r.Id).Select(r => r.Id).ToList();
        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task Paging_is_exact_when_the_provider_returns_ties_in_a_different_order_on_every_call()
    {
        // Arrange — "undefined order" taken literally: each call shuffles runs that share a start time.
        var runs = Enumerable.Range(1, 40)
            .Select(i => new FlowRunRecord { Id = IdOf(i), StartedAt = Start.AddSeconds(-(i % 3)) })
            .ToList();
        IFlowRunStore store = new TieOrderStore(runs, new Random(1234));

        // Act
        var seen = await ReadAllPagesAsync(store, 6);

        // Assert
        var expected = runs.OrderByDescending(r => r.StartedAt).ThenBy(r => r.Id).Select(r => r.Id).ToList();
        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task First_page_is_sorted_into_cursor_order()
    {
        // Arrange
        var runs = Enumerable.Range(1, 5).Select(i => new FlowRunRecord { Id = IdOf(i), StartedAt = Start }).ToList();
        IFlowRunStore store = new TieOrderStore(runs);

        // Act
        var page = await store.SearchRunsAsync(new RunSearchQuery { Take = 10 });

        // Assert
        Assert.Equal(runs.Select(r => r.Id), page.Runs.Select(r => r.Id));
        Assert.False(page.HasMore);
    }

    private static Guid IdOf(int i) => Guid.Parse($"00000000-0000-0000-0000-{i:D12}");

    private static async Task<List<Guid>> ReadAllPagesAsync(IFlowRunStore store, int take)
    {
        var seen = new List<Guid>();
        RunSearchCursor? after = null;
        for (var guard = 0; guard < 1000; guard++)
        {
            var page = await store.SearchRunsAsync(new RunSearchQuery { Take = take, After = after });
            Assert.True(page.Runs.Count <= take);
            seen.AddRange(page.Runs.Select(r => r.Id));
            if (!page.HasMore) break;
            after = page.NextCursor;
        }

        return seen;
    }

    /// <summary>Legacy-only provider: newest first, ties in descending id order or shuffled per call.</summary>
    private sealed class TieOrderStore(List<FlowRunRecord> runs, Random? shuffleTies = null) : IFlowRunStore
    {
        public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId, string? status, int skip, int take, string? search, bool deepSearch, DateTimeOffset? startedFrom = null, DateTimeOffset? startedTo = null)
        {
            var window = runs
                .Where(r => (startedFrom is null || r.StartedAt >= startedFrom) && (startedTo is null || r.StartedAt <= startedTo))
                .OrderByDescending(r => r.StartedAt);
            var filtered = (shuffleTies is null
                    ? window.ThenByDescending(r => r.Id)
                    : window.ThenBy(_ => shuffleTies.Next()))
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
