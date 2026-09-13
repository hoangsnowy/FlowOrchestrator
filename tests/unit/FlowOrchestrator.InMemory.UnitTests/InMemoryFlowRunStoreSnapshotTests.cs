using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Read-path snapshot contract of <see cref="InMemoryFlowRunStore"/>: a record handed to a caller
/// is a point-in-time copy, never the live instance the write path mutates.
/// </summary>
/// <remarks>
/// <para>
/// The store keeps one <c>FlowRunRecord</c> per run and mutates it in place — <c>CompleteRunAsync</c>
/// writes <c>Status</c> and <c>CompletedAt</c> onto the stored object. While the reads returned that
/// object, <c>Status</c> and <c>Steps</c> were sampled at different instants: the caller got the step
/// list as it stood inside the read, then read <c>Status</c> off the live record afterwards. A run
/// that completed in that window presented as terminal with an incomplete step list.
/// </para>
/// <para>
/// That is the CI-only failure in <c>HappyPathTests.LinearFlow_runs_to_completion</c>
/// (<c>Assert.Equal(3, result.Steps.Count)</c> → <c>Actual: 2</c>): the poll in
/// <c>FlowOrchestrator.Testing.Internal.RunPoller</c> does exactly this — reads the detail, then
/// tests <c>run.Status</c> for terminality. Runner CPU contention widened the window enough to hit
/// it. No engine race is involved; the run really did record all three steps before completing.
/// </para>
/// </remarks>
public class InMemoryFlowRunStoreSnapshotTests
{
    private readonly InMemoryFlowRunStore _sut = new();

    [Fact]
    public async Task GetRunDetailAsync_snapshot_does_not_turn_terminal_after_the_read()
    {
        // Arrange — a run with two finished steps and a third that has not started yet, i.e. the
        // exact state a poll can observe one instant before the run completes.
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "step_a", "Echo", null, null);
        await _sut.RecordStepCompleteAsync(runId, "step_a", "Succeeded", null, null);
        await _sut.RecordStepStartAsync(runId, "step_b", "Echo", null, null);
        await _sut.RecordStepCompleteAsync(runId, "step_b", "Succeeded", null, null);

        // Act — read the detail, then let the engine finish the run in the window that follows.
        var snapshot = await _sut.GetRunDetailAsync(runId);
        await _sut.RecordStepStartAsync(runId, "step_c", "Echo", null, null);
        await _sut.RecordStepCompleteAsync(runId, "step_c", "Succeeded", null, null);
        await _sut.CompleteRunAsync(runId, "Succeeded");

        // Assert — the snapshot still pairs the status it was read with against its own step list.
        Assert.NotNull(snapshot);
        Assert.Equal("Running", snapshot!.Status);
        Assert.Null(snapshot.CompletedAt);
        Assert.Equal(2, snapshot.Steps!.Count);
    }

    [Fact]
    public async Task GetRunDetailAsync_read_after_completion_carries_every_step()
    {
        // Arrange — the same run, completed before the read rather than after it.
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        foreach (var stepKey in new[] { "step_a", "step_b", "step_c" })
        {
            await _sut.RecordStepStartAsync(runId, stepKey, "Echo", null, null);
            await _sut.RecordStepCompleteAsync(runId, stepKey, "Succeeded", null, null);
        }

        await _sut.CompleteRunAsync(runId, "Succeeded");

        // Act
        var snapshot = await _sut.GetRunDetailAsync(runId);

        // Assert — a terminal status is never paired with a short step list.
        Assert.NotNull(snapshot);
        Assert.Equal("Succeeded", snapshot!.Status);
        Assert.Equal(3, snapshot.Steps!.Count);
    }

    [Fact]
    public async Task GetRunDetailAsync_does_not_publish_its_step_list_onto_the_stored_record()
    {
        // Arrange — the list views document Steps as null; a detail read must not backfill it.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(flowId, "Flow", runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "step_a", "Echo", null, null);
        await _sut.RecordStepCompleteAsync(runId, "step_a", "Succeeded", null, null);
        _ = await _sut.GetRunDetailAsync(runId);

        // Act
        var (runs, _) = await _sut.GetRunsPageAsync(flowId, null, 0, 10, null);

        // Assert
        var listed = Assert.Single(runs);
        Assert.Null(listed.Steps);
    }

    [Fact]
    public async Task GetRunAsync_snapshot_is_unaffected_by_a_later_completion()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        var header = await _sut.GetRunAsync(runId);

        // Act
        await _sut.CompleteRunAsync(runId, "Failed");

        // Assert
        Assert.NotNull(header);
        Assert.Equal("Running", header!.Status);
        Assert.Equal("Failed", (await _sut.GetRunAsync(runId))!.Status);
    }

    [Fact]
    public async Task Mutating_a_returned_snapshot_does_not_write_through_to_the_store()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        var snapshot = (await _sut.GetRunDetailAsync(runId))!;

        // Act
        snapshot.Status = "Succeeded";
        snapshot.CompletedAt = DateTimeOffset.UtcNow.AddDays(-30);

        // Assert — retention keys off CompletedAt, so a write-through here would purge a live run.
        await _sut.CleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        var stored = await _sut.GetRunAsync(runId);
        Assert.NotNull(stored);
        Assert.Equal("Running", stored!.Status);
    }
}
