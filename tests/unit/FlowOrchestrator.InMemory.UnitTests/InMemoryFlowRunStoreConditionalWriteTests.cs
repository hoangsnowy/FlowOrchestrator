using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// RFC 2.0 P-1 hotfixes on <see cref="InMemoryFlowRunStore"/>: the conditional writes the engine relies
/// on for exactly-once transitions (loop settle, manual retry) and the orphaned-claim release.
/// </summary>
public sealed class InMemoryFlowRunStoreConditionalWriteTests
{
    private readonly InMemoryFlowRunStore _sut = new();

    /// <summary>
    /// Releases <paramref name="contenders"/> calls of <paramref name="attempt"/> at the same instant and
    /// returns every result.
    /// </summary>
    private static async Task<bool[]> ContendAsync(int contenders, Func<Task<bool>> attempt)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task<bool>[contenders];
        for (var i = 0; i < contenders; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await gate.Task;
                return await attempt();
            });
        }

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_MovesARunningStepOnce_ThenReportsFalse()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

        // Act
        var first = await _sut.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", """{"iterations":2}""", null);
        var second = await _sut.TryCompleteRunningStepAsync(runId, "loop", "Skipped", null, "Run is Cancelled.");

        // Assert
        Assert.True(first);
        Assert.False(second);
        var step = await _sut.GetStepAsync(runId, "loop");
        Assert.Equal("Succeeded", step!.Status);
        Assert.Equal("""{"iterations":2}""", step.OutputJson);
        Assert.NotNull(step.CompletedAt);
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_LeavesATerminalOrMissingStepUntouched()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "loop", "ForEach", null, null);
        await _sut.RecordStepCompleteAsync(runId, "loop", "Skipped", null, "Run is Cancelled.");

        // Act
        var terminal = await _sut.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", null, null);
        var missing = await _sut.TryCompleteRunningStepAsync(runId, "never_started", "Succeeded", null, null);

        // Assert
        Assert.False(terminal);
        Assert.False(missing);
        Assert.Equal("Skipped", (await _sut.GetStepAsync(runId, "loop"))!.Status);
    }

    [Fact]
    public async Task TryCompleteRunningStepAsync_UnderContention_HasExactlyOneWinner()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "SettleFlow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

        // Act
        var results = await ContendAsync(64, () =>
            _sut.TryCompleteRunningStepAsync(runId, "loop", "Succeeded", null, null));

        // Assert
        Assert.Equal(1, results.Count(won => won));
        Assert.Equal(StepStatus.Succeeded, (await _sut.GetStepStatusesAsync(runId))["loop"]);
    }

    [Fact]
    public async Task ReleaseOrphanedClaimsAsync_ReleasesOnlyOldClaimsWithoutAStatusRow_AndTheirLedgerRows()
    {
        // Arrange - "orphan": claimed 61 s ago, never started. "started": claimed as long ago, has a row.
        // "young": claimed 1 s ago, no row yet (its worker may still be starting it).
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryFlowRunStore { Clock = clock };
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "OrphanFlow", runId, "manual", null, null);
        foreach (var key in new[] { "orphan", "started" })
        {
            await store.TryRecordDispatchAsync(runId, key);
            await store.TryClaimStepAsync(runId, key);
        }

        await store.RecordStepStartAsync(runId, "started", "Work", null, null);
        clock.Advance(TimeSpan.FromSeconds(60));
        await store.TryRecordDispatchAsync(runId, "young");
        await store.TryClaimStepAsync(runId, "young");
        clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        var released = await store.ReleaseOrphanedClaimsAsync(runId, TimeSpan.FromSeconds(60));

        // Assert
        Assert.Equal(["orphan"], released);
        Assert.False(await store.IsStepClaimedAsync(runId, "orphan"));
        Assert.False(await store.IsStepDispatchedAsync(runId, "orphan"));
        Assert.True(await store.IsStepClaimedAsync(runId, "started"));
        Assert.True(await store.IsStepDispatchedAsync(runId, "started"));
        Assert.True(await store.IsStepClaimedAsync(runId, "young"));
        Assert.True(await store.IsStepDispatchedAsync(runId, "young"));
        Assert.True(await store.TryClaimStepAsync(runId, "orphan"));
    }

    [Fact]
    public async Task ReleaseOrphanedClaimsAsync_WithZeroAge_ReleasesEveryClaimWithoutARow()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "OrphanFlow", runId, "manual", null, null);
        await _sut.TryClaimStepAsync(runId, "a");
        await _sut.TryClaimStepAsync(runId, "b");
        await _sut.RecordStepStartAsync(runId, "b", "Work", null, null);

        // Act
        var released = await _sut.ReleaseOrphanedClaimsAsync(runId, TimeSpan.Zero);
        var none = await _sut.ReleaseOrphanedClaimsAsync(Guid.NewGuid(), TimeSpan.Zero);

        // Assert
        Assert.Equal(["a"], released);
        Assert.Empty(none);
        Assert.True(await _sut.IsStepClaimedAsync(runId, "b"));
    }

    /// <summary>A clock that only moves when the test says so.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        /// <summary>Moves the clock forward.</summary>
        public void Advance(TimeSpan by) => _now += by;

        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
