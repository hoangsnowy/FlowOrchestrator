using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SqlServer.Tests;

public sealed class SqlFlowSignalStoreTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlFlowSignalStore _store;
    private readonly string _connectionString;

    public SqlFlowSignalStoreTests(SqlServerFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
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

    // ── Expiry is judged by the DATABASE clock in the delivery statement itself ────────────────────────

    [Fact]
    public async Task DeliverSignal_at_or_after_expiry_returns_Expired_and_writes_nothing()
    {
        // Arrange — ExpiresAt = the database's "now" read before the delivery; by the time the delivery
        // statement runs the database clock is at or past it, and the boundary is inclusive.
        var runId = Guid.NewGuid();
        var deadline = await DatabaseNowAsync();
        await _store.RegisterWaiterAsync(runId, "wait", "approve", deadline);

        // Act
        var result = await _store.DeliverSignalAsync(runId, "approve", """{"late":true}""");
        var waiter = await _store.GetWaiterAsync(runId, "wait");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Expired, result.Status);
        Assert.Equal("wait", result.StepKey);
        Assert.Null(result.DeliveredAt);
        Assert.Null(waiter!.DeliveredAt);
        Assert.Null(waiter.PayloadJson);
    }

    [Fact]
    public async Task DeliverSignal_just_before_expiry_returns_Delivered()
    {
        // Arrange — a deadline a few seconds ahead of the database clock.
        var runId = Guid.NewGuid();
        var deadline = (await DatabaseNowAsync()).AddSeconds(5);
        await _store.RegisterWaiterAsync(runId, "wait", "approve", deadline);

        // Act
        var result = await _store.DeliverSignalAsync(runId, "approve", """{"ok":true}""");
        var waiter = await _store.GetWaiterAsync(runId, "wait");

        // Assert — the recorded DeliveredAt is earlier than the deadline by construction.
        Assert.Equal(SignalDeliveryStatus.Delivered, result.Status);
        Assert.Equal("wait", result.StepKey);
        Assert.Equal("""{"ok":true}""", waiter!.PayloadJson);
        Assert.True(waiter.DeliveredAt < waiter.ExpiresAt);
    }

    [Fact]
    public async Task DeliverSignal_expired_and_live_waiters_same_signal_live_one_receives_it()
    {
        // Arrange — the OLDER waiter has expired; it must neither take the payload nor block the live one.
        var runId = Guid.NewGuid();
        var now = await DatabaseNowAsync();
        await _store.RegisterWaiterAsync(runId, "expired", "approve", now.AddMinutes(-1));
        await Task.Delay(20); // distinct CreatedAt ordering, as in the two-waiter test above
        await _store.RegisterWaiterAsync(runId, "live", "approve", now.AddHours(1));

        // Act
        var first = await _store.DeliverSignalAsync(runId, "approve", """{"n":1}""");
        var second = await _store.DeliverSignalAsync(runId, "approve", """{"n":2}""");
        var expired = await _store.GetWaiterAsync(runId, "expired");
        var live = await _store.GetWaiterAsync(runId, "live");

        // Assert — the live waiter gets the first delivery; with it delivered, the expired one is what is left.
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);
        Assert.Equal("live", first.StepKey);
        Assert.Equal(SignalDeliveryStatus.Expired, second.Status);
        Assert.Equal("expired", second.StepKey);
        Assert.Null(expired!.DeliveredAt);
        Assert.Null(expired.PayloadJson);
        Assert.Equal("""{"n":1}""", live!.PayloadJson);
    }

    /// <summary>Reads the database clock the store judges expiry by.</summary>
    private async Task<DateTimeOffset> DatabaseNowAsync()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT SYSDATETIMEOFFSET();", conn);
        return (DateTimeOffset)(await cmd.ExecuteScalarAsync())!;
    }
}
