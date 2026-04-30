using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// State-machine invariant tests for run-control transitions on
/// <see cref="InMemoryFlowRunStore"/> (also implements
/// <see cref="FlowOrchestrator.Core.Storage.IFlowRunControlStore"/>).
/// Verifies idempotency of cancel and timeout transitions and the interaction
/// between cancel-requested and timed-out states (Section G2).
/// </summary>
public sealed class RunControlStateMachineTests
{
    [Fact]
    public async Task RequestCancelAsync_AlreadyCancelled_ReturnsFalse()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, null);
        var first = await store.RequestCancelAsync(runId, "user-cancel");

        // Act
        var second = await store.RequestCancelAsync(runId, "duplicate-call");

        // Assert
        Assert.True(first);
        Assert.False(second);
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.True(control!.CancelRequested);
        // The first reason wins — the second call must not overwrite.
        Assert.Equal("user-cancel", control.CancelReason);
    }

    [Fact]
    public async Task MarkTimedOutAsync_AlreadyTimedOut_ReturnsFalse()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var first = await store.MarkTimedOutAsync(runId, "first-timeout");

        // Act
        var second = await store.MarkTimedOutAsync(runId, "duplicate-timeout");

        // Assert
        Assert.True(first);
        Assert.False(second);
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.NotNull(control!.TimedOutAtUtc);
    }

    [Fact]
    public async Task RequestCancelThenMarkTimedOut_TimedOutWins()
    {
        // Arrange — cancel first, then a separate timeout signal arrives.
        // The timed-out timestamp must be set; cancel state remains true (forced by MarkTimedOutAsync).
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var cancelOk = await store.RequestCancelAsync(runId, "user-cancel");

        // Act
        var timeoutOk = await store.MarkTimedOutAsync(runId, "timeout-after-cancel");

        // Assert
        Assert.True(cancelOk);
        Assert.True(timeoutOk);
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.True(control!.CancelRequested);
        Assert.NotNull(control.TimedOutAtUtc);
    }

    [Fact]
    public async Task MarkTimedOutThenRequestCancel_BothSucceedButCancelDoesNotClearTimedOut()
    {
        // Arrange — timeout first, then a user requests cancel.
        // Cancel returns false because MarkTimedOutAsync already set CancelRequested = true.
        // The timed-out timestamp must remain set.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var timeoutOk = await store.MarkTimedOutAsync(runId, "first-timeout");

        // Act
        var cancelOk = await store.RequestCancelAsync(runId, "late-user-cancel");

        // Assert
        Assert.True(timeoutOk);
        Assert.False(cancelOk);
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.True(control!.CancelRequested);
        Assert.NotNull(control.TimedOutAtUtc);
    }
}
