using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression for the missing timeout-enforcement service: a run stuck <c>Running</c> with a lapsed
/// deadline and nothing scheduled must be marked <see cref="RunStatus.TimedOut"/> by the periodic
/// <c>FlowTimeoutEnforcementHostedService</c> — without any manual retry or further step dispatch.
/// Before the fix such a run sat <c>Running</c> indefinitely.
/// </summary>
public sealed class PeriodicTimeoutEnforcementTests
{
    [Fact]
    public async Task PeriodicSweep_MarksStuckRunningRunAsTimedOut()
    {
        // Arrange — host with a fast enforcement interval. We seed a stuck Running run directly in the
        // store (a run whose worker died leaving nothing scheduled), which is the exact production
        // scenario the lazy dispatch-time gate can never resolve.
        await using var host = await FlowTestHost.For<HandlerThrowsFlow>()
            .WithService(new FlakyHandlerProbe())
            .WithHandler<FlakyStepHandler>("Flaky")
            .WithCustomConfiguration(b => b.RunControl.TimeoutEnforcementInterval = TimeSpan.FromSeconds(1))
            .BuildAsync();

        var runStore = host.Services.GetRequiredService<IFlowRunStore>();
        var controlStore = host.Services.GetRequiredService<IFlowRunControlStore>();
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await runStore.StartRunAsync(flowId, "StuckFlow", runId, "manual", null, null);
        await controlStore.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddSeconds(-1));

        // Act — no trigger, no retry: wait for the periodic sweep to enforce the deadline.
        var snapshot = await host.WaitForRunAsync(runId, TimeSpan.FromSeconds(30));

        // Assert — the run was proactively timed out by the hosted service.
        Assert.Equal(RunStatus.TimedOut, snapshot.Status);
        var control = await controlStore.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.NotNull(control!.TimedOutAtUtc);
    }
}
