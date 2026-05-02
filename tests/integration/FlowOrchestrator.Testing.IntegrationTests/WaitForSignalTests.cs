using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// End-to-end tests for the built-in <c>WaitForSignal</c> step type, covering the happy path,
/// timeout, indefinite-wait, multi-waiter, cancellation, duplicate delivery, missing-run/missing-waiter
/// 404s, and downstream payload propagation. All scenarios run against the in-memory storage and
/// runtime via <see cref="FlowTestHost"/>.
/// </summary>
public sealed class WaitForSignalTests
{
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaiterPollTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task HappyPath_SignalArrives_StepSucceeds_DownstreamConsumesPayload()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host);
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        await WaitForWaiterAsync(host, runId, "wait_for_approval");

        // Act
        var delivery = await signals.DispatchAsync(
            runId, "approval", JsonSerializer.Serialize(new { approver = "alice", approved = true }));

        var result = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert
        Assert.Equal(SignalDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("wait_for_approval", delivery.StepKey);
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["wait_for_approval"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["finalize"].Status);
        Assert.Equal("alice", result.Steps["finalize"].Output.GetProperty("Echoed").GetString());
    }

    [Fact]
    public async Task MultipleWaiters_OnlyMatchingSignalResumes_OtherStillPending()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalParallelFlow>()
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host);
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();
        var runStore = host.Services.GetRequiredService<IFlowRunStore>();

        await WaitForWaiterAsync(host, runId, "wait_alpha");
        await WaitForWaiterAsync(host, runId, "wait_beta");

        // Act
        var delivery = await signals.DispatchAsync(runId, "alpha", "{\"who\":\"alpha-caller\"}");
        // Use the snapshot returned by the wait helper instead of re-fetching:
        // a fresh GetRunDetailAsync can race with the engine briefly flipping
        // wait_alpha back to "Running" during a polling re-dispatch — that race
        // is the source of the previous CI flake on this test.
        var run = await WaitForStepStatusAsync(runStore, runId, "wait_alpha", StepStatus.Succeeded);
        var alpha = run!.Steps!.First(s => s.StepKey == "wait_alpha");
        var beta = run.Steps!.First(s => s.StepKey == "wait_beta");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("Succeeded", alpha.Status);
        // beta hasn't received its signal — should be Pending, but tolerate a
        // momentary "Running" sample if the engine just re-dispatched its poll.
        Assert.Contains(beta.Status, new[] { "Pending", "Running" });

        // Cleanup: deliver beta so the run can complete and the host shuts down cleanly.
        await signals.DispatchAsync(runId, "beta", "{}");
        await host.WaitForRunAsync(runId, TerminalTimeout);
    }

    [Fact]
    public async Task Timeout_NoSignal_StepFailsWithDescriptiveReason()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalTimeoutFlow>()
            .WithFastPolling()
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(15));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(StepStatus.Failed, result.Steps["wait"].Status);
        Assert.Contains(
            "Signal 'approval' not received",
            result.Steps["wait"].FailureReason ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTimeout_StepRemainsPending_UntilSignalDelivered()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host);
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();
        var runStore = host.Services.GetRequiredService<IFlowRunStore>();

        await WaitForWaiterAsync(host, runId, "wait_for_approval");

        // Act — observe Pending status persists across multiple polling reschedules.
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        var midRun = await runStore.GetRunDetailAsync(runId);
        Assert.NotNull(midRun);
        var midStep = midRun!.Steps?.FirstOrDefault(s => s.StepKey == "wait_for_approval");
        Assert.Equal(StepStatus.Pending.ToString(), midStep!.Status);

        await signals.DispatchAsync(runId, "approval", "{\"approver\":\"bob\"}");
        var final = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert
        Assert.Equal(RunStatus.Succeeded, final.Status);
    }

    [Fact]
    public async Task DuplicateSignal_SecondDelivery_ReturnsAlreadyDelivered()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host);
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();
        await WaitForWaiterAsync(host, runId, "wait_for_approval");

        // Act
        var first = await signals.DispatchAsync(runId, "approval", "{\"approver\":\"first\"}");
        var second = await signals.DispatchAsync(runId, "approval", "{\"approver\":\"second\"}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);
        Assert.Equal(SignalDeliveryStatus.AlreadyDelivered, second.Status);

        // Cleanup: let the run finish before disposing the host.
        await host.WaitForRunAsync(runId, TerminalTimeout);
    }

    [Fact]
    public async Task SignalForUnknownRun_ReturnsNotFound()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        // Act
        var result = await signals.DispatchAsync(Guid.NewGuid(), "approval", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task SignalForExistingRun_NoMatchingWaiter_ReturnsNotFound()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host);
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();
        await WaitForWaiterAsync(host, runId, "wait_for_approval");

        // Act
        var result = await signals.DispatchAsync(runId, "this-signal-does-not-exist", "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.NotFound, result.Status);

        // Cleanup
        await signals.DispatchAsync(runId, "approval", "{\"approver\":\"cleanup\"}");
        await host.WaitForRunAsync(runId, TerminalTimeout);
    }

    [Fact]
    public async Task RunCancelled_WhileWaiting_StepSkipped_RunCancelled()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host);
        var control = host.Services.GetRequiredService<IFlowRunControlStore>();
        await WaitForWaiterAsync(host, runId, "wait_for_approval");

        // Act
        await control.RequestCancelAsync(runId, "user cancelled");
        var result = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert — engine marks the active step Skipped (with the run-status reason) and the run Cancelled.
        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(StepStatus.Skipped, result.Steps["wait_for_approval"].Status);
    }

    [Fact]
    public async Task DownstreamStep_ReceivesSignalPayloadField()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WaitForSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host);
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();
        await WaitForWaiterAsync(host, runId, "wait_for_approval");

        // Act
        await signals.DispatchAsync(
            runId, "approval", JsonSerializer.Serialize(new { approver = "carol", approved = true }));
        var result = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal("carol", result.Steps["finalize"].Output.GetProperty("Echoed").GetString());
    }

    private static async Task<Guid> StartRunAsync<TFlow>(FlowTestHost<TFlow> host)
        where TFlow : class, IFlowDefinition, new()
    {
        using var scope = host.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
        var flow = scope.ServiceProvider.GetServices<IFlowDefinition>().OfType<TFlow>().First();

        var ctx = new TriggerContext
        {
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null),
            RunId = Guid.Empty
        };
        await orchestrator.TriggerAsync(ctx);
        return ctx.RunId;
    }

    private static async Task<FlowRunRecord?> WaitForStepStatusAsync(IFlowRunStore runStore, Guid runId, string stepKey, StepStatus expected)
    {
        // Monotonic clock via Stopwatch — immune to system-clock adjustments.
        // Wall-clock deadline (DateTimeOffset.UtcNow) was the source of CI flakes
        // when an agent NTP-corrected mid-test.
        // Returns the matching run snapshot so the caller can read other step
        // statuses from the SAME consistent view, avoiding the race where a
        // second GetRunDetailAsync catches the engine mid-redispatch.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < WaiterPollTimeout)
        {
            var run = await runStore.GetRunDetailAsync(runId);
            var step = run?.Steps?.FirstOrDefault(s => s.StepKey == stepKey);
            if (step is not null && step.Status == expected.ToString())
            {
                return run;
            }
            await Task.Delay(50);
        }
        throw new InvalidOperationException(
            $"Timed out waiting for step '{stepKey}' to reach status {expected} (run={runId}).");
    }

    private static async Task WaitForWaiterAsync<TFlow>(FlowTestHost<TFlow> host, Guid runId, string stepKey)
        where TFlow : class, IFlowDefinition, new()
    {
        var store = host.Services.GetRequiredService<IFlowSignalStore>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < WaiterPollTimeout)
        {
            var waiter = await store.GetWaiterAsync(runId, stepKey);
            if (waiter is not null) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException(
            $"Timed out waiting for waiter (run={runId}, step={stepKey}) to be registered.");
    }
}
