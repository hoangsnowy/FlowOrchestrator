using FlowOrchestrator.Core.Notifications;

namespace FlowOrchestrator.Core.Tests.Notifications;

/// <summary>
/// Verifies that the default <see cref="NoopFlowEventNotifier"/> is genuinely a no-op:
/// returns synchronously, never throws, and ignores both well-formed and degenerate inputs.
/// </summary>
public sealed class NoopFlowEventNotifierTests
{
    [Fact]
    public void PublishAsync_returns_completed_value_task_synchronously()
    {
        // Arrange
        var notifier = NoopFlowEventNotifier.Instance;
        var evt = new RunStartedEvent
        {
            RunId = Guid.NewGuid(),
            FlowId = Guid.NewGuid(),
            FlowName = "Demo",
            TriggerKey = "manual"
        };

        // Act
        var task = notifier.PublishAsync(evt);

        // Assert
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PublishAsync_does_not_throw_for_any_event_type()
    {
        // Arrange
        var notifier = NoopFlowEventNotifier.Instance;
        var runId = Guid.NewGuid();
        FlowLifecycleEvent[] events =
        {
            new RunStartedEvent { RunId = runId, FlowId = Guid.NewGuid(), FlowName = "F", TriggerKey = "manual" },
            new StepCompletedEvent { RunId = runId, StepKey = "s1", Status = "Succeeded" },
            new StepCompletedEvent { RunId = runId, StepKey = "s1", Status = "Failed", FailedReason = "boom" },
            new StepRetriedEvent { RunId = runId, StepKey = "s1" },
            new RunCompletedEvent { RunId = runId, Status = "Succeeded" },
            new RunCompletedEvent { RunId = runId, Status = "Cancelled" }
        };

        // Act + Assert (no throw)
        foreach (var evt in events)
        {
            await notifier.PublishAsync(evt);
        }
    }

    [Fact]
    public async Task PublishAsync_honours_cancellation_token_without_throwing()
    {
        // Arrange
        var notifier = NoopFlowEventNotifier.Instance;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var evt = new RunCompletedEvent { RunId = Guid.NewGuid(), Status = "Succeeded" };

        // Act
        await notifier.PublishAsync(evt, cts.Token);

        // Assert — completing without throwing IS the assertion: noop must never block on a token.
    }
}
