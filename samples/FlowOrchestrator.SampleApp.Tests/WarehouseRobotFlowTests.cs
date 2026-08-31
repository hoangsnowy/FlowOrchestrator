using System.Diagnostics;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.SampleApp.Flows;
using FlowOrchestrator.SampleApp.Steps;
using FlowOrchestrator.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.SampleApp.Tests;

/// <summary>
/// Integration test for <see cref="WarehouseRobotFlow"/> — the issue #169 loop-barrier demo.
/// Plays the robot manually (two signals) and asserts the callback step ran strictly after
/// every location was scanned.
/// </summary>
public sealed class WarehouseRobotFlowTests
{
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WarehouseRobotFlow_scansEveryLocation_beforeTheRobotCallbackFires()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WarehouseRobotFlow>()
            .WithHandler<LogMessageStepHandler>("LogMessage")
            .WithHandler<OpenCameraStep>("OpenCamera")
            .WithHandler<RobotCallbackStep>("RobotCallback")
            .WithHandler<ForEachStepHandler>("ForEach")
            .WithFastPolling()
            .BuildAsync();

        var body = new Dictionary<string, object?>
        {
            ["OrderNo"] = "WH-1042",
            ["Locations"] = new[] { "A-01-03", "B-11-01" }
        };

        using var scope = host.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
        var flow = scope.ServiceProvider.GetServices<IFlowDefinition>().OfType<WarehouseRobotFlow>().First();
        var ctx = new TriggerContext
        {
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", body),
            RunId = Guid.Empty,
            TriggerData = body
        };

        // Act — trigger, then play the robot: deliver both arrivals once their waiters park.
        await orchestrator.TriggerAsync(ctx);
        var signals = host.Services.GetRequiredService<IFlowSignalDispatcher>();
        var signalStore = host.Services.GetRequiredService<IFlowSignalStore>();
        foreach (var (index, location) in new[] { (0, "A-01-03"), (1, "B-11-01") })
        {
            var stepKey = $"scan_process.{index}.wait_robot_goto";
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15)
                   && await signalStore.GetWaiterAsync(ctx.RunId, stepKey) is null)
            {
                await Task.Delay(25);
            }

            await signals.DispatchAsync(
                ctx.RunId, "robot_goto", JsonSerializer.Serialize(new { Location = location }));
        }

        var result = await host.WaitForRunAsync(ctx.RunId, TerminalTimeout);

        // Assert — full success, per-iteration capture of the right location, callback last.
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps["scan_process"].Status);

        var callback = result.Steps["robot_callback_success"];
        Assert.Equal(StepStatus.Succeeded, callback.Status);
        Assert.Equal(2, callback.Output.GetProperty("ScannedCount").GetInt32());
        Assert.NotNull(callback.StartedAt);

        foreach (var index in new[] { 0, 1 })
        {
            var camera = result.Steps[$"scan_process.{index}.open_camera"];
            Assert.Equal(StepStatus.Succeeded, camera.Status);
            Assert.NotNull(camera.CompletedAt);
            Assert.True(
                callback.StartedAt >= camera.CompletedAt,
                $"robot_callback_success started at {callback.StartedAt:O} — before iteration {index}'s capture at {camera.CompletedAt:O}.");
        }

        // Each camera photographed the location ITS OWN waiter reported (scope-relative @steps()).
        var captured = new[] { 0, 1 }
            .Select(i => result.Steps[$"scan_process.{i}.open_camera"].Output.GetProperty("Location").GetString() ?? "")
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "A-01-03", "B-11-01" }, captured);
    }
}
