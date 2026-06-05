using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SqlServer.Tests;

public sealed class SqlFlowSignalStoreTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlFlowSignalStore _store;

    public SqlFlowSignalStoreTests(SqlServerFixture fixture)
    {
        _store = new SqlFlowSignalStore(fixture.ConnectionString);
    }

    [Fact]
    public async Task RegisterWaiter_then_GetWaiter_round_trip()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        // Act
        await _store.RegisterWaiterAsync(runId, "step1", "approval", expiresAt);
        var waiter = await _store.GetWaiterAsync(runId, "step1");

        // Assert
        Assert.NotNull(waiter);
        Assert.Equal(runId, waiter!.RunId);
        Assert.Equal("step1", waiter.StepKey);
        Assert.Equal("approval", waiter.SignalName);
        Assert.NotNull(waiter.ExpiresAt);
        Assert.Null(waiter.DeliveredAt);
        Assert.Null(waiter.PayloadJson);
    }

    [Fact]
    public async Task RegisterWaiter_idempotent_on_duplicate_key()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.RegisterWaiterAsync(runId, "step1", "old-signal", null);

        // Act
        await _store.RegisterWaiterAsync(runId, "step1", "new-signal", DateTimeOffset.UtcNow.AddHours(1));
        var waiter = await _store.GetWaiterAsync(runId, "step1");

        // Assert
        Assert.NotNull(waiter);
        Assert.Equal("new-signal", waiter!.SignalName);
        Assert.NotNull(waiter.ExpiresAt);
    }

    [Fact]
    public async Task DeliverSignal_sets_payload_and_returns_Delivered()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.RegisterWaiterAsync(runId, "step_x", "approve", null);

        // Act
        var result = await _store.DeliverSignalAsync(runId, "approve", """{"approved":true}""");
        var waiter = await _store.GetWaiterAsync(runId, "step_x");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Delivered, result.Status);
        Assert.Equal("step_x", result.StepKey);
        Assert.NotNull(result.DeliveredAt);
        Assert.Equal("""{"approved":true}""", waiter!.PayloadJson);
        Assert.NotNull(waiter.DeliveredAt);
    }

    [Fact]
    public async Task DeliverSignal_second_call_returns_AlreadyDelivered()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.RegisterWaiterAsync(runId, "step_y", "approve", null);
        await _store.DeliverSignalAsync(runId, "approve", "{}");

        // Act
        var second = await _store.DeliverSignalAsync(runId, "approve", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.AlreadyDelivered, second.Status);
        Assert.Equal("step_y", second.StepKey);
    }

    [Fact]
    public async Task DeliverSignal_two_waiters_same_signal_deliver_twice_both_resume()
    {
        // Arrange — two waiters on the same RunId + signal name, distinct step keys.
        // The CTE must filter on DeliveredAt IS NULL, otherwise the second delivery keeps
        // re-selecting the first (already-delivered) row and the second waiter is stranded.
        var runId = Guid.NewGuid();
        await _store.RegisterWaiterAsync(runId, "first", "approval", null);
        await Task.Delay(20); // ensure distinct CreatedAt ordering (SYSDATETIMEOFFSET resolution)
        await _store.RegisterWaiterAsync(runId, "second", "approval", null);

        // Act
        var first = await _store.DeliverSignalAsync(runId, "approval", """{"n":1}""");
        var second = await _store.DeliverSignalAsync(runId, "approval", """{"n":2}""");

        // Assert — both deliveries succeed, targeting the two distinct waiters.
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);
        Assert.Equal(SignalDeliveryStatus.Delivered, second.Status);
        Assert.Equal("first", first.StepKey);
        Assert.Equal("second", second.StepKey);

        var firstWaiter = await _store.GetWaiterAsync(runId, "first");
        var secondWaiter = await _store.GetWaiterAsync(runId, "second");
        Assert.NotNull(firstWaiter!.DeliveredAt);
        Assert.NotNull(secondWaiter!.DeliveredAt);
        Assert.Equal("""{"n":1}""", firstWaiter.PayloadJson);
        Assert.Equal("""{"n":2}""", secondWaiter.PayloadJson);

        // A third delivery, with every matching waiter delivered, reports AlreadyDelivered.
        var third = await _store.DeliverSignalAsync(runId, "approval", "{}");
        Assert.Equal(SignalDeliveryStatus.AlreadyDelivered, third.Status);
    }

    [Fact]
    public async Task DeliverSignal_no_waiter_returns_NotFound()
    {
        // Arrange
        var runId = Guid.NewGuid();

        // Act
        var result = await _store.DeliverSignalAsync(runId, "ghost", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.NotFound, result.Status);
        Assert.Null(result.StepKey);
    }

    [Fact]
    public async Task RemoveWaiter_drops_row()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _store.RegisterWaiterAsync(runId, "step_z", "ping", null);

        // Act
        await _store.RemoveWaiterAsync(runId, "step_z");
        var waiter = await _store.GetWaiterAsync(runId, "step_z");

        // Assert
        Assert.Null(waiter);
    }
}
