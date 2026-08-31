using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Idempotency ("tombstone") coverage for <see cref="WaitForSignalHandler"/>: the waiter row is
/// deliberately never removed after delivery or expiry, so a stale re-invocation re-enters the
/// same branch instead of registering a fresh waiter and re-parking.
/// </summary>
/// <remarks>
/// Inside a ForEach body this is load-bearing for the completion barrier. A parked
/// <c>WaitForSignal</c> child releases both its dispatch-ledger row and its execute claim on every
/// <see cref="StepStatus.Pending"/> result, and <see cref="FlowSignalDispatcher"/> schedules the
/// resume attempt without going through the ledger — so two executions of the same iteration step
/// can be queued at once. If the losing one re-registered a waiter and returned Pending, an
/// already-terminal iteration would flip back to non-terminal <i>after</i> its loop barrier had
/// settled, leaving the loop step Succeeded while one of its children is Pending.
/// </remarks>
public sealed class WaitForSignalHandlerTombstoneTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class SignalStepInstance(string key, WaitForSignalInput inputs) : IStepInstance<WaitForSignalInput>
    {
        public Guid RunId { get; set; }
        public string? PrincipalId { get; set; }
        public object? TriggerData { get; set; }
        public IReadOnlyDictionary<string, string>? TriggerHeaders { get; set; }
        public string? JobId { get; set; }
        public DateTimeOffset ScheduledTime { get; set; }
        public string Type { get; set; } = "WaitForSignal";
        public string Key { get; } = key;
        public WaitForSignalInput Inputs { get; set; } = inputs;
        public int Index { get; set; }
        public bool ScopeMoveNext { get; set; }
    }

    private static IFlowDefinition Flow() => Substitute.For<IFlowDefinition>();

    private static IExecutionContext Context(Guid runId) =>
        new Core.Execution.ExecutionContext { RunId = runId };

    [Fact]
    public async Task FirstInvocationInsideALoopIteration_registersTheWaiterAndParks()
    {
        // Arrange
        var store = new InMemoryFlowSignalStore();
        var handler = new WaitForSignalHandler(store, new FixedClock(Now));
        var runId = Guid.NewGuid();
        var step = new SignalStepInstance(
            "scan.0.wait", new WaitForSignalInput { SignalName = "robot_goto", TimeoutSeconds = 30 });

        // Act
        var result = (StepResult)(await handler.ExecuteAsync(Context(runId), Flow(), step))!;

        // Assert
        Assert.Equal(StepStatus.Pending, result.Status);
        var waiter = await store.GetWaiterAsync(runId, "scan.0.wait");
        Assert.NotNull(waiter);
        Assert.Equal("robot_goto", waiter!.SignalName);
        Assert.Null(waiter.DeliveredAt);
    }

    [Fact]
    public async Task StaleReInvocationAfterDelivery_returnsSucceededAgain_andNeverReParks()
    {
        // Arrange - the iteration parked, the signal was delivered, and the step already ran to
        // Succeeded. A poll queued before the delivery now arrives late.
        var store = new InMemoryFlowSignalStore();
        var handler = new WaitForSignalHandler(store, new FixedClock(Now));
        var runId = Guid.NewGuid();
        var step = new SignalStepInstance(
            "scan.0.wait", new WaitForSignalInput { SignalName = "robot_goto", TimeoutSeconds = 30 });

        await handler.ExecuteAsync(Context(runId), Flow(), step);
        await store.DeliverSignalAsync(runId, "robot_goto", "{\"Location\":\"BAY-A\"}");
        var first = (StepResult)(await handler.ExecuteAsync(Context(runId), Flow(), step))!;

        // Act
        var stale = (StepResult)(await handler.ExecuteAsync(Context(runId), Flow(), step))!;

        // Assert - both invocations succeed with the same payload; the step never returns to a
        // non-terminal status, which is what keeps a settled loop barrier settled.
        Assert.Equal(StepStatus.Succeeded, first.Status);
        Assert.Equal(StepStatus.Succeeded, stale.Status);
        Assert.Equal(
            "BAY-A",
            ((JsonElement)stale.Result!).GetProperty("Location").GetString());
    }

    [Fact]
    public async Task StaleReInvocationAfterExpiry_returnsFailedAgain_andNeverReParks()
    {
        // Arrange - a 5s waiter registered at Now, re-invoked 10s later.
        var store = new InMemoryFlowSignalStore();
        var runId = Guid.NewGuid();
        var step = new SignalStepInstance(
            "scan.0.wait", new WaitForSignalInput { SignalName = "robot_goto", TimeoutSeconds = 5 });

        await new WaitForSignalHandler(store, new FixedClock(Now)).ExecuteAsync(Context(runId), Flow(), step);
        var expired = new WaitForSignalHandler(store, new FixedClock(Now.AddSeconds(10)));

        // Act
        var first = (StepResult)(await expired.ExecuteAsync(Context(runId), Flow(), step))!;
        var stale = (StepResult)(await expired.ExecuteAsync(Context(runId), Flow(), step))!;

        // Assert - the expired waiter is a tombstone: a stale poll fails again rather than
        // registering a brand-new waiter and parking the iteration a second time.
        Assert.Equal(StepStatus.Failed, first.Status);
        Assert.Equal(StepStatus.Failed, stale.Status);
        Assert.Contains("not received within 5s", stale.FailedReason);
    }

    [Fact]
    public async Task DeliveryAfterExpiryButBeforeTheTimeoutPoll_winsOverTheExpiryBranch()
    {
        // Arrange - the deadline lapsed, but the signal lands before the timeout poll runs.
        var store = new InMemoryFlowSignalStore();
        var runId = Guid.NewGuid();
        var step = new SignalStepInstance(
            "scan.0.wait", new WaitForSignalInput { SignalName = "robot_goto", TimeoutSeconds = 5 });

        await new WaitForSignalHandler(store, new FixedClock(Now)).ExecuteAsync(Context(runId), Flow(), step);
        await store.DeliverSignalAsync(runId, "robot_goto", "{\"Location\":\"LATE\"}");

        // Act
        var result = (StepResult)(await new WaitForSignalHandler(store, new FixedClock(Now.AddSeconds(10)))
            .ExecuteAsync(Context(runId), Flow(), step))!;

        // Assert - the delivered branch is evaluated before the expiry branch, so a signal that
        // beat the poll is honoured rather than discarded.
        Assert.Equal(StepStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task IterationsSharingASignalName_receiveOneDeliveryEach_inRegistrationOrder()
    {
        // Arrange - issue #169's manifest gives every iteration the same signal name.
        var store = new InMemoryFlowSignalStore();
        var runId = Guid.NewGuid();
        var handler = new WaitForSignalHandler(store, new FixedClock(Now));

        await handler.ExecuteAsync(Context(runId), Flow(),
            new SignalStepInstance("scan.0.wait", new WaitForSignalInput { SignalName = "robot_goto" }));
        await handler.ExecuteAsync(Context(runId), Flow(),
            new SignalStepInstance("scan.1.wait", new WaitForSignalInput { SignalName = "robot_goto" }));

        // Act
        var first = await store.DeliverSignalAsync(runId, "robot_goto", "{\"Location\":\"A\"}");
        var second = await store.DeliverSignalAsync(runId, "robot_goto", "{\"Location\":\"B\"}");
        var third = await store.DeliverSignalAsync(runId, "robot_goto", "{\"Location\":\"C\"}");

        // Assert - each iteration is released exactly once; a third delivery has no waiter left
        // and must not re-open an already-terminal iteration.
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);
        Assert.Equal(SignalDeliveryStatus.Delivered, second.Status);
        Assert.NotEqual(first.StepKey, second.StepKey);
        Assert.Equal(SignalDeliveryStatus.AlreadyDelivered, third.Status);
    }
}
