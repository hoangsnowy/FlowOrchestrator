using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression coverage for issue #169: a step declaring <c>RunAfter = { loop: [Succeeded] }</c>
/// must run <b>after</b> every ForEach iteration finished, not in parallel with them.
/// </summary>
/// <remarks>
/// Ordering is asserted against logical events, never wall-clock: the downstream step is checked
/// while the loop is provably mid-flight (one iteration finished, the other still parked on its
/// signal), which is a happens-before the old fire-and-forget loop could not satisfy — it
/// dispatched the downstream step at fan-out time, before any child had even started.
/// </remarks>
public sealed class ForEachLoopBarrierTests
{
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StepPollTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task DownstreamOfForEach_doesNotRun_untilEveryIterationIsTerminal()
    {
        // Arrange — issue #169's manifest: two iterations, each parking on "robot_goto",
        // and robot_callback_success gated on the loop step.
        await using var host = await FlowTestHost.For<ForEachLoopBarrierFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host, new Dictionary<string, object?>
        {
            ["Steps"] = new[] { "loc-a", "loc-b" }
        });
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        await WaitForWaiterAsync(host, runId, "scan_process.0.wait_robot_goto");
        await WaitForWaiterAsync(host, runId, "scan_process.1.wait_robot_goto");

        // Both iterations parked ⇒ the loop fanned out and its children already executed once.
        // Pre-fix, robot_callback_success was enqueued ahead of them and would have a row here.
        Assert.Null(await FindStepAsync(host, runId, "robot_callback_success"));
        Assert.Equal(StepStatus.Running.ToString(), (await FindStepAsync(host, runId, "scan_process"))?.Status);

        // Act — release only the first iteration and let it run to completion.
        var first = await signals.DispatchAsync(
            runId, "robot_goto", JsonSerializer.Serialize(new { Location = "BAY-A" }));
        var firstIteration = first.StepKey![..first.StepKey!.LastIndexOf('.')];
        await WaitForStepStatusAsync(host, runId, $"{firstIteration}.open_camera", StepStatus.Succeeded);

        // Assert — one iteration done, the other still parked: the barrier must still hold.
        Assert.Null(await FindStepAsync(host, runId, "robot_callback_success"));
        Assert.Equal(StepStatus.Running.ToString(), (await FindStepAsync(host, runId, "scan_process"))?.Status);

        // Act — release the last iteration; only now may the downstream step run.
        var second = await signals.DispatchAsync(
            runId, "robot_goto", JsonSerializer.Serialize(new { Location = "BAY-B" }));
        var result = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert — run completed, loop settled, downstream ran strictly after every child.
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);
        Assert.Equal(SignalDeliveryStatus.Delivered, second.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["scan_process"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["robot_callback_success"].Status);

        var callbackStartedAt = result.Steps["robot_callback_success"].StartedAt;
        Assert.NotNull(callbackStartedAt);
        foreach (var iteration in new[] { 0, 1 })
        {
            var camera = result.Steps[$"scan_process.{iteration}.open_camera"];
            Assert.Equal(StepStatus.Succeeded, camera.Status);
            Assert.NotNull(camera.CompletedAt);
            Assert.True(
                callbackStartedAt >= camera.CompletedAt,
                $"robot_callback_success started at {callbackStartedAt:O} — before iteration {iteration} completed at {camera.CompletedAt:O}.");
        }
    }

    [Fact]
    public async Task ForEachWithFastChildren_stillSettlesLoop_andRunsDownstreamLast()
    {
        // Arrange — no signals involved: the barrier must also hold for the ordinary fast path,
        // where the old behaviour raced instead of visibly reordering.
        await using var host = await FlowTestHost.For<ForEachTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(
            body: new { items = new[] { "a", "b", "c", "d", "e" } },
            timeout: TerminalTimeout);

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["process_items"].Status);

        var finalizeStartedAt = result.Steps["finalize"].StartedAt;
        Assert.NotNull(finalizeStartedAt);
        for (var index = 0; index < 5; index++)
        {
            var iterate = result.Steps[$"process_items.{index}.iterate"];
            Assert.Equal(StepStatus.Succeeded, iterate.Status);
            Assert.NotNull(iterate.CompletedAt);
            Assert.True(
                finalizeStartedAt >= iterate.CompletedAt,
                $"finalize started at {finalizeStartedAt:O} — before iteration {index} completed at {iterate.CompletedAt:O}.");
        }
    }

    private static async Task<Guid> StartRunAsync<TFlow>(FlowTestHost<TFlow> host, object body)
        where TFlow : class, IFlowDefinition, new()
    {
        using var scope = host.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
        var flow = scope.ServiceProvider.GetServices<IFlowDefinition>().OfType<TFlow>().First();

        var ctx = new TriggerContext
        {
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", body),
            RunId = Guid.Empty,
            TriggerData = body
        };
        await orchestrator.TriggerAsync(ctx);
        return ctx.RunId;
    }

    private static async Task<FlowStepRecord?> FindStepAsync<TFlow>(FlowTestHost<TFlow> host, Guid runId, string stepKey)
        where TFlow : class, IFlowDefinition, new()
    {
        var runStore = host.Services.GetRequiredService<IFlowRunStore>();
        var detail = await runStore.GetRunDetailAsync(runId);
        return detail?.Steps?.FirstOrDefault(step => string.Equals(step.StepKey, stepKey, StringComparison.Ordinal));
    }

    private static async Task WaitForWaiterAsync<TFlow>(FlowTestHost<TFlow> host, Guid runId, string stepKey)
        where TFlow : class, IFlowDefinition, new()
    {
        // Monotonic clock via Stopwatch — same anti-flake pattern as ForEachSignalSiblingTests.
        var store = host.Services.GetRequiredService<IFlowSignalStore>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StepPollTimeout)
        {
            if (await store.GetWaiterAsync(runId, stepKey) is not null) return;
            await Task.Delay(25);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for waiter (run={runId}, step={stepKey}). {await DumpStepsAsync(host, runId)}");
    }

    private static async Task WaitForStepStatusAsync<TFlow>(
        FlowTestHost<TFlow> host, Guid runId, string stepKey, StepStatus expected)
        where TFlow : class, IFlowDefinition, new()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StepPollTimeout)
        {
            var step = await FindStepAsync(host, runId, stepKey);
            if (step is not null && string.Equals(step.Status, expected.ToString(), StringComparison.Ordinal)) return;
            await Task.Delay(25);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for step '{stepKey}' to reach {expected} (run={runId}). {await DumpStepsAsync(host, runId)}");
    }

    private static async Task<string> DumpStepsAsync<TFlow>(FlowTestHost<TFlow> host, Guid runId)
        where TFlow : class, IFlowDefinition, new()
    {
        var runStore = host.Services.GetRequiredService<IFlowRunStore>();
        var detail = await runStore.GetRunDetailAsync(runId);
        var stepDump = detail?.Steps is null
            ? "(no steps)"
            : string.Join(" | ", detail.Steps.Select(step => $"{step.StepKey}={step.Status}"));
        return $"RunStatus={detail?.Status}; Steps: {stepDump}";
    }
}
