using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 3 — manual retry yields AttemptCount = 2 after one failure + one retry.</summary>
public sealed class RetryTests
{
    [Fact]
    public async Task Manual_retry_after_failure_reports_AttemptCount_of_2()
    {
        // Arrange
        var counter = new FlakyCounter();
        await using var host = await FlowTestHost.For<FlakyFlow>()
            .WithService(counter)
            .WithHandler<FlakyStepHandler>("Flaky")
            .BuildAsync();

        var flow = host.Services.GetServices<IFlowDefinition>().OfType<FlakyFlow>().Single();
        var orchestrator = host.Services.GetRequiredService<IFlowOrchestrator>();

        // Act — first run fails, then we manually retry the failing step.
        var first = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(RunStatus.Failed, first.Status);
        Assert.Equal(1, first.Steps["flaky"].AttemptCount);

        await orchestrator.RetryStepAsync(flow.Id, first.RunId, "flaky");
        var second = await host.WaitForRunAsync(first.RunId, TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(RunStatus.Succeeded, second.Status);
        Assert.Equal(StepStatus.Succeeded, second.Steps["flaky"].Status);
        Assert.Equal(2, second.AttemptCount("flaky"));
    }
}
