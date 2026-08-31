using System.Diagnostics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// End-to-end regression coverage for cancelling / timing out a run whose ForEach loop is parked
/// on its completion barrier.
/// </summary>
/// <remarks>
/// The barrier keeps the loop step <see cref="StepStatus.Running"/> until every iteration is
/// terminal, and <c>HasInFlightWorkAsync</c> counts a Running step as in-flight work. The
/// run-control termination gate at the top of <c>RunStepAsync</c> returns before the graph
/// continuation, so nothing there settles the barrier — the loop must therefore be abandoned by
/// the gate itself. Without that, a cancelled run of issue #169's own manifest stays
/// <c>Running</c> forever (until a host restart lets the recovery service close it).
/// </remarks>
public sealed class ForEachLoopBarrierRunControlTests
{
    // 45 s matches ForEachLoopBarrierEdgeCaseTests: the assertion is on a logical outcome (the run
    // reaching a terminal status), never on elapsed time, so a generous budget only absorbs CI
    // CPU contention.
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StepPollTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CancellingARunWhoseForEachIsParkedOnASignal_CompletesTheRunAsCancelled()
    {
        // Arrange — issue #169's manifest: two iterations, each parked on "wait_robot_goto", with
        // "open_camera" gated behind it inside the same iteration and "robot_callback_success"
        // gated on the loop step.
        await using var host = await FlowTestHost.For<ForEachLoopBarrierFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host, new Dictionary<string, object?>
        {
            ["Steps"] = new[] { "loc-a", "loc-b" }
        });

        await WaitForWaiterAsync(host, runId, "scan_process.0.wait_robot_goto");
        await WaitForWaiterAsync(host, runId, "scan_process.1.wait_robot_goto");
        Assert.Equal(StepStatus.Running.ToString(), (await FindStepAsync(host, runId, "scan_process"))?.Status);

        // Act — cancel while both iterations are parked and nothing will ever signal them.
        var control = host.Services.GetRequiredService<IFlowRunControlStore>();
        await control.RequestCancelAsync(runId, "cancelled by operator");
        var result = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert — the run must reach a terminal status instead of parking on a loop step whose
        // iterations can never complete, and the step gated on the loop must never run.
        Assert.False(result.TimedOut, $"run never reached a terminal status. {await DumpStepsAsync(host, runId)}");
        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(StepStatus.Skipped, result.Steps["scan_process"].Status);
        Assert.False(result.Steps.ContainsKey("robot_callback_success"));
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
        // Monotonic clock via Stopwatch, and a generous budget: this waits for a logical event
        // (the waiter row appearing), never asserting an upper bound on elapsed time.
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
