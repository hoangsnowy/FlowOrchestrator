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

    /// <summary>Legacy-only provider: newest first, ties returned in descending id order.</summary>
    private sealed class TieOrderStore(List<FlowRunRecord> runs) : IFlowRunStore
    {
        public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId, string? status, int skip, int take, string? search, bool deepSearch, DateTimeOffset? startedFrom = null, DateTimeOffset? startedTo = null)
        {
            var filtered = runs
                .Where(r => (startedFrom is null || r.StartedAt >= startedFrom) && (startedTo is null || r.StartedAt <= startedTo))
                .OrderByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.Id)
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
