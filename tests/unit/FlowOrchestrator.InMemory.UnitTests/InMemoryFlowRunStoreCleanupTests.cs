using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Retention behaviour of <see cref="InMemoryFlowRunStore.CleanupAsync"/>.
/// </summary>
/// <remarks>
/// The store keeps per-run secondary indexes (<c>_stepKeysByRun</c>, <c>_claimsByRun</c>,
/// <c>_dispatchesByRun</c>) alongside its flat dictionaries, and the hot-path readers consult the
/// indexes rather than the flat maps. Retention that purges only the flat maps therefore leaves a
/// purged run still reporting steps, claims and dispatches — and leaks memory that grows with every
/// completed run in a long-lived host. These tests pin both halves down.
/// </remarks>
public class InMemoryFlowRunStoreCleanupTests
{
    private readonly InMemoryFlowRunStore _sut = new();

    [Fact]
    public async Task CleanupAsync_clears_the_dispatch_ledger_and_every_per_run_index()
    {
        // Arrange — one fully finished run carrying a step row, a claim and a dispatch row.
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "a", "Work", null, null);
        await _sut.TryClaimStepAsync(runId, "a");
        await _sut.TryRecordDispatchAsync(runId, "a");
        await _sut.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
        await _sut.CompleteRunAsync(runId, "Succeeded");
        (await _sut.GetRunDetailAsync(runId))!.CompletedAt = DateTimeOffset.UtcNow.AddDays(-30);

        // Act
        await _sut.CleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert
        Assert.Null(await _sut.GetRunDetailAsync(runId));
        Assert.Empty(await _sut.GetDispatchedStepKeysAsync(runId));
        Assert.Empty(await _sut.GetClaimedStepKeysAsync(runId));
        Assert.Empty(await _sut.GetStepStatusesAsync(runId));
    }

    [Fact]
    public async Task CleanupAsync_does_not_leave_dispatch_rows_behind_as_runs_accumulate()
    {
        // Arrange — many finished runs, each contributing one dispatch-ledger entry. The ledger is
        // keyed independently of the step rows, so purging steps alone leaves every one of these.
        const int RunCount = 50;
        var runIds = new List<Guid>(RunCount);
        for (var i = 0; i < RunCount; i++)
        {
            var id = Guid.NewGuid();
            runIds.Add(id);
            await _sut.StartRunAsync(Guid.NewGuid(), "Flow", id, "manual", null, null);
            await _sut.RecordStepStartAsync(id, "a", "Work", null, null);
            await _sut.TryRecordDispatchAsync(id, "a");
            await _sut.RecordStepCompleteAsync(id, "a", "Succeeded", null, null);
            await _sut.CompleteRunAsync(id, "Succeeded");
            (await _sut.GetRunDetailAsync(id))!.CompletedAt = DateTimeOffset.UtcNow.AddDays(-30);
        }

        // Act
        await _sut.CleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert — no purged run may still report a dispatched step.
        foreach (var id in runIds)
        {
            Assert.Null(await _sut.GetRunDetailAsync(id));
            Assert.Empty(await _sut.GetDispatchedStepKeysAsync(id));
        }
    }

    [Fact]
    public async Task CleanupAsync_retains_a_dispatch_row_whose_run_is_still_inside_the_window()
    {
        // Arrange — one purgeable run and one recent run, so the sweep must discriminate rather
        // than clearing the ledger wholesale.
        var oldRun = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", oldRun, "manual", null, null);
        await _sut.TryRecordDispatchAsync(oldRun, "a");
        await _sut.CompleteRunAsync(oldRun, "Succeeded");
        (await _sut.GetRunDetailAsync(oldRun))!.CompletedAt = DateTimeOffset.UtcNow.AddDays(-30);

        var recentRun = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", recentRun, "manual", null, null);
        await _sut.TryRecordDispatchAsync(recentRun, "a");

        // Act
        await _sut.CleanupAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        // Assert
        Assert.Empty(await _sut.GetDispatchedStepKeysAsync(oldRun));
        Assert.Contains("a", await _sut.GetDispatchedStepKeysAsync(recentRun));
    }
}
