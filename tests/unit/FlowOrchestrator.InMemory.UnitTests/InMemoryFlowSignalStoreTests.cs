using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;

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

    // ── Expiry: a waiter whose ExpiresAt has passed by the store's clock never takes a payload ──────────

    private static readonly DateTimeOffset Deadline = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A settable clock, so the tests pin the store's "now" to exact instants around a deadline.</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task DeliverSignalAsync_AtOrAfterExpiry_ReturnsExpiredAndWritesNothing()
    {
        // Arrange — the boundary is inclusive, as in WaitForSignalHandler: now == ExpiresAt is expired.
        var clock = new ManualClock(Deadline.AddSeconds(-30));
        var store = new InMemoryFlowSignalStore(clock);
        var runId = Guid.NewGuid();
        await store.RegisterWaiterAsync(runId, "wait", "approval", Deadline);
        clock.Now = Deadline;

        // Act
        var result = await store.DeliverSignalAsync(runId, "approval", """{"late":true}""");
        var waiter = await store.GetWaiterAsync(runId, "wait");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Expired, result.Status);
        Assert.Equal("wait", result.StepKey);
        Assert.Null(result.DeliveredAt);
        Assert.Null(waiter!.DeliveredAt);
        Assert.Null(waiter.PayloadJson);
    }

    [Fact]
    public async Task DeliverSignalAsync_JustBeforeExpiry_ReturnsDelivered()
    {
        // Arrange — one tick before the deadline is still live.
        var clock = new ManualClock(Deadline.AddSeconds(-30));
        var store = new InMemoryFlowSignalStore(clock);
        var runId = Guid.NewGuid();
        await store.RegisterWaiterAsync(runId, "wait", "approval", Deadline);
        clock.Now = Deadline.AddTicks(-1);

        // Act
        var result = await store.DeliverSignalAsync(runId, "approval", """{"ok":true}""");
        var waiter = await store.GetWaiterAsync(runId, "wait");

        // Assert — DeliveredAt is the store's clock, so it is earlier than the deadline by construction.
        Assert.Equal(SignalDeliveryStatus.Delivered, result.Status);
        Assert.Equal(Deadline.AddTicks(-1), result.DeliveredAt);
        Assert.Equal("""{"ok":true}""", waiter!.PayloadJson);
        Assert.True(waiter.DeliveredAt < waiter.ExpiresAt);
    }

    [Fact]
    public async Task DeliverSignalAsync_ExpiredAndLiveWaitersSameName_LiveOneReceivesIt()
    {
        // Arrange — the OLDER waiter has expired; it must neither take the payload nor block the live one.
        var clock = new ManualClock(Deadline.AddSeconds(-30));
        var store = new InMemoryFlowSignalStore(clock);
        var runId = Guid.NewGuid();
        await store.RegisterWaiterAsync(runId, "expired", "approval", Deadline);
        clock.Now = Deadline.AddSeconds(-20);
        await store.RegisterWaiterAsync(runId, "live", "approval", Deadline.AddHours(1));
        clock.Now = Deadline.AddSeconds(5);

        // Act
        var first = await store.DeliverSignalAsync(runId, "approval", """{"n":1}""");
        var second = await store.DeliverSignalAsync(runId, "approval", """{"n":2}""");
        var expired = await store.GetWaiterAsync(runId, "expired");

        // Assert — the live waiter gets the first delivery; with it delivered, the expired one is what is left.
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);
        Assert.Equal("live", first.StepKey);
        Assert.Equal(SignalDeliveryStatus.Expired, second.Status);
        Assert.Equal("expired", second.StepKey);
        Assert.Null(expired!.DeliveredAt);
        Assert.Null(expired.PayloadJson);
    }

    [Fact]
    public async Task DeliverSignalAsync_WaitersWithEqualCreatedAt_AreServedInRegistrationOrder()
    {
        // Arrange — a frozen clock stamps every waiter with the same CreatedAt.
        var store = new InMemoryFlowSignalStore(new ManualClock(Deadline));
        var runId = Guid.NewGuid();
        for (var i = 0; i < 8; i++)
        {
            await store.RegisterWaiterAsync(runId, "w" + i, "approval", expiresAt: null);
        }

        // Act
        var keys = new List<string?>();
        for (var i = 0; i < 8; i++)
        {
            keys.Add((await store.DeliverSignalAsync(runId, "approval", "{}")).StepKey);
        }

        // Assert
        Assert.Equal(["w0", "w1", "w2", "w3", "w4", "w5", "w6", "w7"], keys);
    }

    [Fact]
    public async Task UseInMemory_StoreJudgesExpiryByTheRegisteredTimeProvider()
    {
        // Arrange — the handler resolves the registered TimeProvider; the store must judge by the same one.
        // A frozen clock far in the past (FlowTestHostBuilder.WithSystemClock) would otherwise disagree with the
        // wall clock by months.
        var clock = new ManualClock(Deadline.AddSeconds(-30));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        new FlowOrchestratorBuilder(services).UseInMemory();
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IFlowSignalStore>();
        var runId = Guid.NewGuid();
        await store.RegisterWaiterAsync(runId, "wait", "approval", Deadline);
        clock.Now = Deadline.AddSeconds(1);

        // Act
        var result = await store.DeliverSignalAsync(runId, "approval", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Expired, result.Status);
        Assert.Equal(Deadline.AddSeconds(-30), (await store.GetWaiterAsync(runId, "wait"))!.CreatedAt);
    }
}
