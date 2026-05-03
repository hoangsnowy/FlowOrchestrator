using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Testing.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// Regression for the disabled-flow silent-skip invariant: triggering a flow whose
/// <see cref="FlowDefinitionRecord.IsEnabled"/> is <see langword="false"/> must return
/// <c>{ runId: null, disabled: true }</c> without dispatching any step or persisting a run.
/// Reference: CLAUDE.md "Execution Flow" §1.
/// </summary>
public sealed class DisabledFlowSilentSkipTests
{
    [Fact]
    public async Task TriggerAsync_AfterFlowIsDisabled_HandlerIsNotInvoked()
    {
        // Arrange
        var probe = new DisabledFlowInvocationProbe();
        await using var host = await FlowTestHost.For<SimpleManualFlow>()
            .WithService(probe)
            .WithHandler<ProbeStepHandler>("Probe")
            .BuildAsync();

        // Disable the flow via the FlowStore (bypasses the dashboard / API surface).
        var flowStore = host.Services.GetRequiredService<IFlowStore>();
        var flow = new SimpleManualFlow();
        await flowStore.SetEnabledAsync(flow.Id, enabled: false);

        // Act — trigger budget kept short; the silent-skip path returns immediately.
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));

        // Assert — no handler invocation; the wait timed out because no run was ever persisted.
        Assert.Equal(0, probe.Calls);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task TriggerAsync_WhenFlowIsEnabled_HandlerRunsExactlyOnce()
    {
        // Arrange — sanity baseline: with the default IsEnabled = true, the same fixture runs.
        var probe = new DisabledFlowInvocationProbe();
        await using var host = await FlowTestHost.For<SimpleManualFlow>()
            .WithService(probe)
            .WithHandler<ProbeStepHandler>("Probe")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(30));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(1, probe.Calls);
    }
}
