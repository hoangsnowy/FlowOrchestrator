using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Concurrency stress tests for <see cref="InMemoryFlowRunStore"/> verifying that
/// the atomic primitives backing the "Dispatch Many, Execute Once" guarantee — namely
/// <see cref="InMemoryFlowRunStore.TryRecordDispatchAsync"/>,
/// <see cref="InMemoryFlowRunStore.TryClaimStepAsync"/>, and
/// <see cref="InMemoryFlowRunStore.TryRegisterIdempotencyKeyAsync"/> — elect exactly
/// one winner under high parallelism (Section G3 concurrency).
/// </summary>
public sealed class InMemoryFlowRunStoreConcurrencyTests
{
    [Fact]
    public async Task TryRecordDispatchAsync_ParallelCallsForSameStep_ExactlyOneReturnsTrue()
    {
        // Arrange
        const int parallelism = 64;
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act — spawn N tasks that all try to record the same dispatch the moment the gate opens.
        var tasks = Enumerable.Range(0, parallelism)
            .Select(async _ =>
            {
                await startGate.Task;
                return await store.TryRecordDispatchAsync(runId, "step1");
            })
            .ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert — exactly one wins.
        Assert.Equal(1, results.Count(x => x));
        Assert.Equal(parallelism - 1, results.Count(x => !x));
    }

    [Fact]
    public async Task TryClaimStepAsync_ParallelCallsForSameStep_ExactlyOneReturnsTrue()
    {
        // Arrange
        const int parallelism = 64;
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var tasks = Enumerable.Range(0, parallelism)
            .Select(async _ =>
            {
                await startGate.Task;
                return await store.TryClaimStepAsync(runId, "step1");
            })
            .ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, results.Count(x => x));
    }

    [Fact]
    public async Task TryRegisterIdempotencyKeyAsync_ParallelCallsForSameKey_ExactlyOneReturnsTrue()
    {
        // Arrange
        const int parallelism = 32;
        var store = new InMemoryFlowRunStore();
        var flowId = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act — N concurrent triggers for the same idempotency key, each with a distinct RunId.
        var tasks = Enumerable.Range(0, parallelism)
            .Select(async _ =>
            {
                await startGate.Task;
                return await store.TryRegisterIdempotencyKeyAsync(
                    flowId, "manual", "shared-key", Guid.NewGuid());
            })
            .ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert — first writer wins; others observe duplicate and back off to dedup path.
        Assert.Equal(1, results.Count(x => x));
        Assert.Equal(parallelism - 1, results.Count(x => !x));
    }
}
