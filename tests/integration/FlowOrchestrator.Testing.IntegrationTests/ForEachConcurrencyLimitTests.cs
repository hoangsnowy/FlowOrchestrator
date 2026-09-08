using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression coverage for issue #181: <c>ConcurrencyLimit</c> must bound how many ForEach
/// iterations are <b>in flight</b>, not merely stagger their dispatch.
/// </summary>
/// <remarks>
/// <para>
/// The pre-fix implementation dispatched every iteration up front and pushed bucket <c>n</c> back
/// by <c>n × 100 ms</c>. With a body that parks — a <c>WaitForSignal</c>, a polling step — that
/// bound nothing: 100 ms later every iteration was waiting concurrently, so a manifest declaring
/// <c>ConcurrencyLimit = 1</c> ran its whole body in parallel.
/// </para>
/// <para>
/// The assertions are on logical state, never elapsed time: a not-yet-admitted iteration has no
/// signal waiter and no step row at all, which is a fact about the run, not a race. Both directions
/// are covered — a gate that always admitted one iteration would satisfy the sequential test alone.
/// </para>
/// </remarks>
public sealed class ForEachConcurrencyLimitTests
{
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StepPollTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConcurrencyLimitOne_AdmitsTheNextIterationOnlyAfterThePreviousOneIsTerminal()
    {
        // Arrange — issue #181's manifest: three items, each iteration parking mid-body.
        await using var host = await FlowTestHost.For<ForEachSequentialSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host, new Dictionary<string, object?>
        {
            ["Steps"] = new[] { "p-0", "p-1", "p-2" }
        });
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        // Act + Assert — walk the loop one iteration at a time. Before each release, the NEXT
        // iteration must be entirely absent: no waiter, and not one step row.
        for (var index = 0; index < 3; index++)
        {
            await WaitForWaiterAsync(host, runId, $"polish_process.{index}.wait_robot_polish");

            for (var later = index + 1; later < 3; later++)
            {
                Assert.Null(await FindWaiterAsync(host, runId, $"polish_process.{later}.wait_robot_polish"));
                Assert.Empty(await FindIterationStepsAsync(host, runId, $"polish_process.{later}."));
            }

            // The step gated on the loop must not run while iterations remain.
            Assert.Null(await FindStepAsync(host, runId, "polish_done"));

            var delivery = await signals.DispatchAsync(
                runId, ForEachConcurrencyManifest.SignalName,
                JsonSerializer.Serialize(new { Point = $"pt-{index}" }));
            Assert.Equal(SignalDeliveryStatus.Delivered, delivery.Status);
            Assert.Equal($"polish_process.{index}.wait_robot_polish", delivery.StepKey);
        }

        var result = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert — every iteration ran, in order, and the downstream step ran last.
        Assert.False(result.TimedOut, $"run never reached a terminal status. {await DumpStepsAsync(host, runId)}");
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["polish_process"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["polish_done"].Status);

        for (var index = 0; index < 3; index++)
        {
            var done = result.Steps[$"polish_process.{index}.robot_polish_done"];
            Assert.Equal(StepStatus.Succeeded, done.Status);
            Assert.Equal($"pt-{index}", done.Output.GetProperty("Echoed").GetString());
        }

        // Strict serialisation: iteration n+1 started only after iteration n finished.
        for (var index = 0; index < 2; index++)
        {
            var finished = result.Steps[$"polish_process.{index}.robot_polish_done"].CompletedAt;
            var nextStarted = result.Steps[$"polish_process.{index + 1}.robot_polish_start"].StartedAt;
            Assert.NotNull(finished);
            Assert.NotNull(nextStarted);
            Assert.True(
                nextStarted >= finished,
                $"iteration {index + 1} started at {nextStarted:O} — before iteration {index} finished at {finished:O}.");
        }
    }

    [Fact]
    public async Task ConcurrencyLimitTwo_KeepsTwoIterationsInFlightAndAdmitsTheThirdAsOneFinishes()
    {
        // Arrange — same body, limit raised to 2.
        await using var host = await FlowTestHost.For<ForEachPairedSignalFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .WithHandler<ForEachStepHandler>("ForEach")
            .WithFastPolling()
            .BuildAsync();

        var runId = await StartRunAsync(host, new Dictionary<string, object?>
        {
            ["Steps"] = new[] { "p-0", "p-1", "p-2" }
        });
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        // Act + Assert — iterations 0 and 1 park together; 2 must not exist yet.
        await WaitForWaiterAsync(host, runId, "polish_process.0.wait_robot_polish");
        await WaitForWaiterAsync(host, runId, "polish_process.1.wait_robot_polish");
        Assert.Empty(await FindIterationStepsAsync(host, runId, "polish_process.2."));

        // Releasing one frees exactly one slot.
        var first = await signals.DispatchAsync(
            runId, ForEachConcurrencyManifest.SignalName, JsonSerializer.Serialize(new { Point = "pt-a" }));
        Assert.Equal(SignalDeliveryStatus.Delivered, first.Status);

        await WaitForWaiterAsync(host, runId, "polish_process.2.wait_robot_polish");

        // Act — drain the remaining two waiters.
        for (var remaining = 0; remaining < 2; remaining++)
        {
            var delivery = await signals.DispatchAsync(
                runId, ForEachConcurrencyManifest.SignalName, JsonSerializer.Serialize(new { Point = "pt-z" }));
            Assert.Equal(SignalDeliveryStatus.Delivered, delivery.Status);
        }

        var result = await host.WaitForRunAsync(runId, TerminalTimeout);

        // Assert
        Assert.False(result.TimedOut, $"run never reached a terminal status. {await DumpStepsAsync(host, runId)}");
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["polish_process"].Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["polish_done"].Status);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

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

    private static async Task<IReadOnlyList<FlowStepRecord>> FindIterationStepsAsync<TFlow>(
        FlowTestHost<TFlow> host, Guid runId, string runtimeKeyPrefix)
        where TFlow : class, IFlowDefinition, new()
    {
        var runStore = host.Services.GetRequiredService<IFlowRunStore>();
        var detail = await runStore.GetRunDetailAsync(runId);
        return detail?.Steps?
            .Where(step => step.StepKey.StartsWith(runtimeKeyPrefix, StringComparison.Ordinal))
            .ToList() ?? [];
    }

    private static async Task<object?> FindWaiterAsync<TFlow>(FlowTestHost<TFlow> host, Guid runId, string stepKey)
        where TFlow : class, IFlowDefinition, new()
        => await host.Services.GetRequiredService<IFlowSignalStore>().GetWaiterAsync(runId, stepKey);

    private static async Task WaitForWaiterAsync<TFlow>(FlowTestHost<TFlow> host, Guid runId, string stepKey)
        where TFlow : class, IFlowDefinition, new()
    {
        // Monotonic clock via Stopwatch — same anti-flake pattern as ForEachLoopBarrierTests.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StepPollTimeout)
        {
            if (await FindWaiterAsync(host, runId, stepKey) is not null) return;
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
