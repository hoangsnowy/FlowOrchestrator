using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryFlowSignalStore"/>, focusing on the
/// "fan out to the oldest undelivered waiter" contract shared with the SQL backends.
/// </summary>
public class InMemoryFlowSignalStoreTests
{
    private readonly InMemoryFlowSignalStore _sut = new();

    [Fact]
    public async Task DeliverSignalAsync_SingleWaiter_ReturnsDeliveredAndSetsPayload()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.RegisterWaiterAsync(runId, "step1", "approval", expiresAt: null);

        // Act
        var result = await _sut.DeliverSignalAsync(runId, "approval", """{"ok":true}""");
        var waiter = await _sut.GetWaiterAsync(runId, "step1");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Delivered, result.Status);
        Assert.Equal("step1", result.StepKey);
        Assert.NotNull(result.DeliveredAt);
        Assert.Equal("""{"ok":true}""", waiter!.PayloadJson);
        Assert.NotNull(waiter.DeliveredAt);
    }

    [Fact]
    public async Task DeliverSignalAsync_NoWaiter_ReturnsNotFound()
    {
        // Arrange
        var runId = Guid.NewGuid();

        // Act
        var result = await _sut.DeliverSignalAsync(runId, "ghost", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.NotFound, result.Status);
        Assert.Null(result.StepKey);
    }

    [Fact]
    public async Task DeliverSignalAsync_SingleWaiter_SecondCall_ReturnsAlreadyDelivered()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.RegisterWaiterAsync(runId, "step1", "approval", expiresAt: null);
        await _sut.DeliverSignalAsync(runId, "approval", "{}");

        // Act
        var second = await _sut.DeliverSignalAsync(runId, "approval", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.AlreadyDelivered, second.Status);
        Assert.Equal("step1", second.StepKey);
    }

    [Fact]
    public async Task DeliverSignalAsync_TwoWaiters_SameSignal_DeliverTwice_BothResume()
    {
        // Arrange — two waiters on the same run + signal name, distinct step keys. The first
        // registered (oldest CreatedAt) must receive the first delivery, the second the second.
        var runId = Guid.NewGuid();
        await _sut.RegisterWaiterAsync(runId, "first", "approval", expiresAt: null);
        await _sut.RegisterWaiterAsync(runId, "second", "approval", expiresAt: null);

        // Act
        var first = await _sut.DeliverSignalAsync(runId, "approval", """{"n":1}""");
        var second = await _sut.DeliverSignalAsync(runId, "approval", """{"n":2}""");

        // Assert — both deliveries succeed and target different waiters (the bug returned
        // AlreadyDelivered for the 2nd call, stranding the 2nd waiter forever).
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);
        Assert.Equal(SignalDeliveryStatus.Delivered, second.Status);
        Assert.Equal("first", first.StepKey);
        Assert.Equal("second", second.StepKey);

        var firstWaiter = await _sut.GetWaiterAsync(runId, "first");
        var secondWaiter = await _sut.GetWaiterAsync(runId, "second");
        Assert.NotNull(firstWaiter!.DeliveredAt);
        Assert.NotNull(secondWaiter!.DeliveredAt);
        Assert.Equal("""{"n":1}""", firstWaiter.PayloadJson);
        Assert.Equal("""{"n":2}""", secondWaiter.PayloadJson);
    }

    [Fact]
    public async Task DeliverSignalAsync_TwoWaiters_ThirdCall_ReturnsAlreadyDelivered()
    {
        // Arrange — two waiters, both delivered.
        var runId = Guid.NewGuid();
        await _sut.RegisterWaiterAsync(runId, "first", "approval", expiresAt: null);
        await _sut.RegisterWaiterAsync(runId, "second", "approval", expiresAt: null);
        await _sut.DeliverSignalAsync(runId, "approval", "{}");
        await _sut.DeliverSignalAsync(runId, "approval", "{}");

        // Act — once every matching waiter is delivered, further calls report AlreadyDelivered.
        var third = await _sut.DeliverSignalAsync(runId, "approval", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.AlreadyDelivered, third.Status);
    }

    [Fact]
    public async Task RemoveWaiterAsync_DropsWaiter()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.RegisterWaiterAsync(runId, "stepZ", "ping", expiresAt: null);

        // Act
        await _sut.RemoveWaiterAsync(runId, "stepZ");
        var waiter = await _sut.GetWaiterAsync(runId, "stepZ");

        // Assert
        Assert.Null(waiter);
    }
}
