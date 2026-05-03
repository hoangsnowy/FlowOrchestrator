using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests.Storage;

/// <summary>
/// Concurrency stress for ForEach sub-step dispatch atomicity. ForEach expands a parent
/// step into N runtime sub-steps with keys shaped <c>{parent}.{index}.{child}</c>. Multiple
/// replicas may try to dispatch the same sub-step concurrently; the dispatch ledger guards
/// must elect exactly one winner per <c>(runId, sub-step-key)</c>.
///
/// Models the production race by hammering <see cref="InMemoryFlowRunStore.TryRecordDispatchAsync"/>
/// with the canonical ForEach sub-step key shape across many concurrent contestants.
/// </summary>
public sealed class ForEachSubStepDispatchConcurrencyTests
{
    [Fact]
    public async Task TryRecordDispatchAsync_ManyReplicasRacingOnAllForEachSubSteps_EachKeyHasExactlyOneWinner()
    {
        // Arrange — 8-item ForEach × 4 racing replicas = 32 dispatch attempts, only 8 should win.
        const int items = 8;
        const int replicas = 4;
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();

        var subStepKeys = Enumerable.Range(0, items)
            .Select(i => $"loop.{i}.child")
            .ToArray();

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act — every replica races to dispatch every sub-step.
        var tasks = (
            from r in Enumerable.Range(0, replicas)
            from key in subStepKeys
            select Task.Run(async () =>
            {
                await startGate.Task;
                return (Key: key, Won: await store.TryRecordDispatchAsync(runId, key));
            })
        ).ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert — exactly one winner per sub-step key, total winners = items.
        Assert.Equal(items, results.Count(r => r.Won));
        foreach (var key in subStepKeys)
        {
            var winnersForKey = results.Where(r => r.Key == key && r.Won).Count();
            Assert.Equal(1, winnersForKey);
        }
    }

    [Fact]
    public async Task TryClaimStepAsync_ManyReplicasRacingOnSingleForEachSubStep_OnlyOneClaimsExecution()
    {
        // Arrange — 64 replicas race to execute the same sub-step instance.
        const int parallelism = 64;
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        const string subStepKey = "loop.3.child";
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var tasks = Enumerable.Range(0, parallelism)
            .Select(async _ =>
            {
                await startGate.Task;
                return await store.TryClaimStepAsync(runId, subStepKey);
            })
            .ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert — exactly one execution claim per sub-step (the "Execute Once" half).
        Assert.Equal(1, results.Count(x => x));
    }
}
