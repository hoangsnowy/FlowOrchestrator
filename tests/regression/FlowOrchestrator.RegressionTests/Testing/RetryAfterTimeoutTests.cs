using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression for the "dashboard Retry is a permanent no-op past the timeout deadline" defect.
/// A run whose <c>DefaultRunTimeout</c> lapsed while it sat <see cref="RunStatus.Failed"/> must, on
/// retry, get a fresh execution window so the handler actually re-runs and the run can reach
/// <see cref="RunStatus.Succeeded"/> — instead of being skipped by the termination gate forever.
/// </summary>
public sealed class RetryAfterTimeoutTests
{
    [Fact]
    public async Task RetryStepAsync_AfterRunDeadlineLapsed_ReExecutesHandlerAndSucceeds()
    {
        // Arrange — short run timeout; handler fails on attempt 1, succeeds afterwards. The periodic
        // enforcer is disabled here so this test isolates the retry path (a Failed run is not active
        // and would not be swept anyway).
        var probe = new FlakyHandlerProbe { FailUntilAttempt = 1 };
        await using var host = await FlowTestHost.For<HandlerThrowsFlow>()
            .WithService(probe)
            .WithHandler<FlakyStepHandler>("Flaky")
            .WithCustomConfiguration(b =>
            {
                b.RunControl.DefaultRunTimeout = TimeSpan.FromSeconds(2);
                b.RunControl.TimeoutEnforcementInterval = null;
            })
            .BuildAsync();

        // Act 1 — initial trigger fails fast and lands in Failed.
        var initial = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(30));
        Assert.Equal(RunStatus.Failed, initial.Status);
        Assert.Equal(1, probe.Attempt);

        // Let the run's 2s deadline lapse while it sits Failed — this is the state that used to make
        // the run permanently unrecoverable.
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var orchestrator = host.Services.GetRequiredService<IFlowOrchestrator>();
        var flow = new HandlerThrowsFlow();

        // Act 2 — dashboard Retry after the deadline lapsed.
        await orchestrator.RetryStepAsync(flow.Id, initial.RunId, "flaky");
        var snapshot = await host.WaitForRunAsync(initial.RunId, TimeSpan.FromSeconds(30));

        // Assert — the handler ran again (attempt 2) and the run recovered to Succeeded.
        Assert.Equal(RunStatus.Succeeded, snapshot.Status);
        Assert.Equal(2, probe.Attempt);
    }
}
