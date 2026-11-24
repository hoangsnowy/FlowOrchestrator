using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// The default <see cref="IFlowRunStore.IsStepDispatchedAsync"/> body keeps third-party providers that
/// only implement <see cref="IFlowRunStore.GetDispatchedStepKeysAsync"/> correct: it answers from the
/// full ledger read, so the engine's point-lookup admission gate (#189) behaves the same on them.
/// </summary>
public sealed class IsStepDispatchedDefaultImplementationTests
{
    [Fact]
    public async Task Default_answers_membership_from_the_full_ledger_read()
    {
        // Arrange
        var runId = Guid.NewGuid();
        IFlowRunStore store = new LedgerOnlyRunStore(runId, ["loop.0.child", "loop.1.child"]);

        // Act
        var dispatched = await store.IsStepDispatchedAsync(runId, "loop.1.child");
        var notDispatched = await store.IsStepDispatchedAsync(runId, "loop.2.child");
        var otherRun = await store.IsStepDispatchedAsync(Guid.NewGuid(), "loop.1.child");

        // Assert
        Assert.True(dispatched);
        Assert.False(notDispatched);
        Assert.False(otherRun);
    }

    /// <summary>A provider that implements the ledger read but not the point lookup.</summary>
    private sealed class LedgerOnlyRunStore(Guid ownerRunId, string[] keys) : IFlowRunStore
    {
        public Task<IReadOnlySet<string>> GetDispatchedStepKeysAsync(Guid id)
            => Task.FromResult<IReadOnlySet<string>>(id == ownerRunId
                ? new HashSet<string>(keys, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal));

        public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId, string? status, int skip, int take, string? search, bool deepSearch, DateTimeOffset? startedFrom = null, DateTimeOffset? startedTo = null) => throw new NotSupportedException();
        public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(Guid? flowId = null, string? status = null, int skip = 0, int take = 50, string? search = null) => throw new NotSupportedException();
        public Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId, Guid? sourceRunId = null) => throw new NotSupportedException();
        public Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId) => throw new NotSupportedException();
        public Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage) => throw new NotSupportedException();
        public Task<bool> TryCompleteRunningStepAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage) => throw new NotSupportedException();
        public Task CompleteRunAsync(Guid runId, string status) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50) => throw new NotSupportedException();
        public Task<FlowRunRecord?> GetRunDetailAsync(Guid runId) => throw new NotSupportedException();
        public Task<DashboardStatistics> GetStatisticsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync() => throw new NotSupportedException();
        public Task RetryStepAsync(Guid runId, string stepKey) => throw new NotSupportedException();
        public Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReleaseDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
