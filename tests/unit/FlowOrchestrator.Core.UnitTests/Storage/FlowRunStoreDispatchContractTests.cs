using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// Tests for the dispatch-ledger contract of <see cref="InMemoryFlowRunStore"/>:
/// <see cref="FlowOrchestrator.Core.Storage.IFlowRunStore.TryRecordDispatchAsync"/>,
/// <see cref="FlowOrchestrator.Core.Storage.IFlowRunStore.ReleaseDispatchAsync"/>, and
/// <see cref="FlowOrchestrator.Core.Storage.IFlowRunStore.GetDispatchedStepKeysAsync"/>.
/// </summary>
public sealed class FlowRunStoreDispatchContractTests
{
    [Fact]
    public async Task TryRecordDispatchAsync_FirstCallForStep_ReturnsTrue()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();

        // Act
        var result = await store.TryRecordDispatchAsync(runId, "step1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TryRecordDispatchAsync_SecondCallSameStep_ReturnsFalse()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.TryRecordDispatchAsync(runId, "step1");

        // Act
        var second = await store.TryRecordDispatchAsync(runId, "step1");

        // Assert
        Assert.False(second);
    }

    [Fact]
    public async Task ReleaseDispatchAsync_AfterRelease_AllowsReRecording()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.TryRecordDispatchAsync(runId, "step1");
        await store.ReleaseDispatchAsync(runId, "step1");

        // Act
        var canDispatchAgain = await store.TryRecordDispatchAsync(runId, "step1");

        // Assert
        Assert.True(canDispatchAgain);
    }

    [Fact]
    public async Task GetDispatchedStepKeysAsync_ReflectsDispatchedAndReleasedSteps()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.TryRecordDispatchAsync(runId, "step1");
        await store.TryRecordDispatchAsync(runId, "step2");
        await store.TryRecordDispatchAsync(runId, "step3");
        await store.ReleaseDispatchAsync(runId, "step2");

        // Act
        var keys = await store.GetDispatchedStepKeysAsync(runId);

        // Assert
        Assert.Equal(2, keys.Count);
        Assert.Contains("step1", keys);
        Assert.Contains("step3", keys);
        Assert.DoesNotContain("step2", keys);
    }
}
