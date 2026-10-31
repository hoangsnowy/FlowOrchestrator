using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// The default <see cref="IFlowRunStore.SearchRunsAsync"/> body keeps third-party providers that only
/// implement the legacy <c>GetRunsPageAsync</c> correct: <c>Take + 1</c> for <c>HasMore</c>, an exact
/// run-id match, and no total for text searches.
/// </summary>
public sealed class RunSearchDefaultImplementationTests
{
    [Fact]
    public async Task Default_reads_take_plus_one_and_trims_to_take()
    {
        // Arrange
        var store = new LegacyRunStore(Runs(3));

        // Act
        var page = await ((IFlowRunStore)store).SearchRunsAsync(new RunSearchQuery { Text = "x", Take = 2, IncludeTotal = true });

        // Assert
        Assert.Equal(3, store.LastTake);
        Assert.Equal(2, page.Runs.Count);
        Assert.True(page.HasMore);
        Assert.Null(page.TotalCount);
        Assert.Equal("x", store.LastSearch);
    }

    [Fact]
    public async Task Default_forwards_filters_and_returns_total_for_plain_queries()
    {
        // Arrange
        var store = new LegacyRunStore(Runs(1), total: 41);
        var flowId = Guid.NewGuid();
        var from = DateTimeOffset.UtcNow.AddDays(-1);

        // Act
        var page = await ((IFlowRunStore)store).SearchRunsAsync(new RunSearchQuery
        {
            FlowId = flowId, Status = "Failed", StartedFrom = from, Skip = 10, Take = 5, IncludeTotal = true,
        });

        // Assert
        Assert.Equal(41, page.TotalCount);
        Assert.False(page.HasMore);
        Assert.Equal(flowId, store.LastFlowId);
        Assert.Equal("Failed", store.LastStatus);
        Assert.Equal(from, store.LastFrom);
        Assert.Equal(10, store.LastSkip);
        Assert.False(store.LastDeep);
    }

    [Fact]
    public async Task Default_narrows_a_run_id_to_the_exact_match()
    {
        // Arrange — the legacy overload only knows substring search, so the default must filter.
        var runs = Runs(2);
        var store = new LegacyRunStore(runs);

        // Act
        var page = await ((IFlowRunStore)store).SearchRunsAsync(new RunSearchQuery { RunId = runs[1].Id });

        // Assert
        Assert.Equal(runs[1].Id, Assert.Single(page.Runs).Id);
        Assert.Equal(runs[1].Id.ToString("D"), store.LastSearch);
    }

    [Fact]
    public async Task Default_passes_the_step_payload_flag()
    {
        // Arrange
        var store = new LegacyRunStore(Runs(0));

        // Act
        await ((IFlowRunStore)store).SearchRunsAsync(new RunSearchQuery { Text = "tx", IncludeStepPayloads = true });

        // Assert
        Assert.True(store.LastDeep);
    }

    private static FlowRunRecord[] Runs(int count)
        => Enumerable.Range(0, count).Select(_ => new FlowRunRecord { Id = Guid.NewGuid(), Status = "Failed" }).ToArray();

    /// <summary>A provider written before <see cref="IFlowRunStore.SearchRunsAsync"/> existed.</summary>
    private sealed class LegacyRunStore(IReadOnlyList<FlowRunRecord> runs, int total = 0) : IFlowRunStore
    {
        public Guid? LastFlowId { get; private set; }
        public string? LastStatus { get; private set; }
        public string? LastSearch { get; private set; }
        public int LastSkip { get; private set; }
        public int LastTake { get; private set; }
        public bool LastDeep { get; private set; }
        public DateTimeOffset? LastFrom { get; private set; }

        public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId, string? status, int skip, int take, string? search, bool deepSearch, DateTimeOffset? startedFrom = null, DateTimeOffset? startedTo = null)
        {
            (LastFlowId, LastStatus, LastSkip, LastTake, LastSearch, LastDeep, LastFrom) = (flowId, status, skip, take, search, deepSearch, startedFrom);
            IReadOnlyList<FlowRunRecord> page = runs.Take(take).ToList();
            return Task.FromResult((page, total));
        }

        public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId = null, string? status = null, int skip = 0, int take = 50, string? search = null)
            => throw new NotSupportedException();
        public Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId, Guid? sourceRunId = null) => throw new NotSupportedException();
        public Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId) => throw new NotSupportedException();
        public Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage) => throw new NotSupportedException();
        public Task CompleteRunAsync(Guid runId, string status) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50) => throw new NotSupportedException();
        public Task<FlowRunRecord?> GetRunDetailAsync(Guid runId) => throw new NotSupportedException();
        public Task<DashboardStatistics> GetStatisticsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync() => throw new NotSupportedException();
        public Task RetryStepAsync(Guid runId, string stepKey) => throw new NotSupportedException();
        public Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReleaseDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> GetDispatchedStepKeysAsync(Guid runId) => throw new NotSupportedException();
    }
}
