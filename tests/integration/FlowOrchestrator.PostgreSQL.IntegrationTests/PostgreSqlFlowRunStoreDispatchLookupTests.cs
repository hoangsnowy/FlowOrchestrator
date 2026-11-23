namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// <see cref="PostgreSqlFlowRunStore.IsStepDispatchedAsync"/> against a real PostgreSQL: the point lookup
/// the engine's <c>ForEach</c> admission gate uses instead of reading the whole ledger (#189) must agree
/// with <see cref="PostgreSqlFlowRunStore.GetDispatchedStepKeysAsync"/> on record, release and run scoping.
/// </summary>
public sealed partial class PostgreSqlFlowRunStoreTests
{
    [Fact]
    public async Task IsStepDispatchedAsync_tracks_record_and_release()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.TryRecordDispatchAsync(runId, "loop.3.child");

        // Act
        var whileDispatched = await _store.IsStepDispatchedAsync(runId, "loop.3.child");
        var otherKey = await _store.IsStepDispatchedAsync(runId, "loop.4.child");
        var otherRun = await _store.IsStepDispatchedAsync(Guid.NewGuid(), "loop.3.child");
        await _store.ReleaseDispatchAsync(runId, "loop.3.child");
        var afterRelease = await _store.IsStepDispatchedAsync(runId, "loop.3.child");

        // Assert
        Assert.True(whileDispatched);
        Assert.False(otherKey);
        Assert.False(otherRun);
        Assert.False(afterRelease);
    }

    [Fact]
    public async Task IsStepDispatchedAsync_agrees_with_the_full_ledger_read()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var keys = Enumerable.Range(0, 12).Select(i => $"loop.{i}.child").ToArray();
        foreach (var key in keys)
        {
            await _store.TryRecordDispatchAsync(runId, key);
        }

        await _store.ReleaseDispatchAsync(runId, keys[5]);

        // Act
        var ledger = await _store.GetDispatchedStepKeysAsync(runId);
        var pointAnswers = new List<bool>();
        foreach (var key in keys)
        {
            pointAnswers.Add(await _store.IsStepDispatchedAsync(runId, key));
        }

        // Assert
        Assert.Equal(keys.Select(ledger.Contains), pointAnswers);
        Assert.False(pointAnswers[5]);
    }
}
