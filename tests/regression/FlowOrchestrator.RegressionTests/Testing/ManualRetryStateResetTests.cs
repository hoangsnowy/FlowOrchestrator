using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression for the manual retry contract (<see cref="IFlowOrchestrator.RetryStepAsync"/>):
/// after a failure, calling RetryStepAsync must reset the step's claim and run-store state so
/// the handler is re-invoked. Repeated retries followed by eventual success must transition
/// the run cleanly to <see cref="RunStatus.Succeeded"/> — the engine's claim ledger must NOT
/// leak state across attempts.
///
/// (FlowOrchestrator does not have a built-in auto-retry policy; retries are explicitly
/// initiated by the dashboard / API. This test exercises the engine's reset semantics rather
/// than backoff timing.)
/// </summary>
public sealed class ManualRetryStateResetTests
{
    [Fact]
    public async Task RetryStepAsync_AfterMultipleFailures_EventuallySucceedsWithCleanState()
    {
        // Arrange — handler fails the first 3 attempts then succeeds.
        var probe = new FlakyHandlerProbe { FailUntilAttempt = 3 };
        await using var host = await FlowTestHost.For<HandlerThrowsFlow>()
            .WithService(probe)
            .WithHandler<FlakyStepHandler>("Flaky")
            .BuildAsync();

        // Act — initial trigger lands in Failed (1 attempt).
        var initial = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(30));
        Assert.False(initial.TimedOut);
        Assert.Equal(RunStatus.Failed, initial.Status);
        Assert.Equal(1, probe.Attempt);

        var orchestrator = host.Services.GetRequiredService<IFlowOrchestrator>();
        var flow = new HandlerThrowsFlow();

        // Two more retries fail, the fourth succeeds.
        for (var i = 0; i < 3; i++)
        {
            await orchestrator.RetryStepAsync(flow.Id, initial.RunId, "flaky");
            var snapshot = await host.WaitForRunAsync(initial.RunId, TimeSpan.FromSeconds(30));

            if (i < 2)
            {
                // Still inside the failure budget.
                Assert.Equal(RunStatus.Failed, snapshot.Status);
            }
            else
            {
                Assert.Equal(RunStatus.Succeeded, snapshot.Status);
            }
        }

        // Assert — exactly four attempts: 1 trigger + 3 retries.
        Assert.Equal(4, probe.Attempt);
    }
}
