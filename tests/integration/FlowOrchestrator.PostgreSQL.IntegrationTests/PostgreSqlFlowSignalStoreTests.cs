using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.PostgreSQL.Tests;

public sealed class PostgreSqlFlowSignalStoreTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFlowSignalStore _store;

    public PostgreSqlFlowSignalStoreTests(PostgreSqlFixture fixture)
    {
        _store = new PostgreSqlFlowSignalStore(fixture.ConnectionString);
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
