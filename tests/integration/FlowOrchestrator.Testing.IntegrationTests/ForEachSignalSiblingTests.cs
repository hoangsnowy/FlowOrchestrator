using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Proves the exact issue #166 scenario end-to-end: inside a ForEach loop, a
/// <c>WaitForSignal</c> child parks, is resumed by a signal, and its sibling reads the
/// signal payload via the bare-key expression <c>@steps('wait_robot_goto').output.Location</c>.
/// Each delivery carries a distinct payload, and the assertion pins each iteration's
/// consumer to the payload delivered to <b>its own</b> waiter — so a wrong-scope resolve
/// (e.g. always reading iteration 0) fails the test, not just a thrown expression.
/// </summary>
public sealed class ForEachSignalSiblingTests
{
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaiterPollTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task WaitForSignalSiblingInsideForEach_ConsumerReadsOwnIterationsSignalPayload()
    {
        // Arrange — the issue's manifest shape: two iterations, both waiting on "robot_goto".
        await using var host = await FlowTestHost.For<ForEachSignalSiblingFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .WithFastPolling()
            .BuildAsync();

        // Dictionary keys survive Web-defaults serialization verbatim; an anonymous type's
        // "Steps" property would camelCase to "steps" and silently miss the (case-sensitive)
        // @triggerBody()?.Steps path — the issue reporter's payload arrives as raw JSON with
        // "Steps" capitalized, so this mirrors their trigger exactly.
        var runId = await StartRunAsync(host, new Dictionary<string, object?>
        {
            ["Steps"] = new[] { "loc-a", "loc-b" }
        });
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        await WaitForWaiterAsync(host, runId, "scan_process.0.wait_robot_goto");
        await WaitForWaiterAsync(host, runId, "scan_process.1.wait_robot_goto");

        // Act — two deliveries with distinct payloads; DispatchAsync reports which
        // runtime waiter (scan_process.{i}.wait_robot_goto) consumed each one.
        var first = await signals.DispatchAsync(
            runId, "robot_goto", JsonSerializer.Serialize(new { Location = "BAY-A" }));
        var second = await signals.DispatchAsync(
            runId, "robot_goto", JsonSerializer.Serialize(new { Location = "BAY-B" }));

        var result = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert — run completes, both deliveries landed on distinct waiters.
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);
        Assert.Equal(SignalDeliveryStatus.Delivered, second.Status);
        Assert.NotEqual(first.StepKey, second.StepKey);

        // Each iteration's open_camera echoed the Location delivered to ITS OWN waiter —
        // the direct proof that @steps('wait_robot_goto') resolved scope-relatively.
        foreach (var (delivery, expectedLocation) in new[] { (first, "BAY-A"), (second, "BAY-B") })
        {
            var iterationPrefix = delivery.StepKey![..delivery.StepKey!.LastIndexOf('.')];
            var camera = result.Steps[$"{iterationPrefix}.open_camera"];
            Assert.Equal(StepStatus.Succeeded, camera.Status);
            Assert.Equal(expectedLocation, camera.Output.GetProperty("Echoed").GetString());
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

    private static async Task WaitForWaiterAsync<TFlow>(FlowTestHost<TFlow> host, Guid runId, string stepKey)
        where TFlow : class, IFlowDefinition, new()
    {
        // Monotonic clock via Stopwatch — same anti-flake pattern as WaitForSignalTests.
        var store = host.Services.GetRequiredService<IFlowSignalStore>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < WaiterPollTimeout)
        {
            var waiter = await store.GetWaiterAsync(runId, stepKey);
            if (waiter is not null) return;
            await Task.Delay(25);
        }
        var runStore = host.Services.GetRequiredService<IFlowRunStore>();
        var detail = await runStore.GetRunDetailAsync(runId);
        var stepDump = detail?.Steps is null
            ? "(no steps)"
            : string.Join(" | ", detail.Steps.Select(s => $"{s.StepKey}={s.Status}"));
        throw new InvalidOperationException(
            $"Timed out waiting for waiter (run={runId}, step={stepKey}). RunStatus={detail?.Status}; Steps: {stepDump}");
    }
}
