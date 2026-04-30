using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 9 — calling TriggerAsync after DisposeAsync surfaces ObjectDisposedException.</summary>
public sealed class DisposalTests
{
    [Fact]
    public async Task TriggerAsync_after_DisposeAsync_throws_ObjectDisposedException()
    {
        // Arrange
        var host = await FlowTestHost.For<LinearTestFlow>()
            .WithHandler<EchoStepHandler>("Echo")
            .BuildAsync();

        await host.DisposeAsync();

        // Act + Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => host.TriggerAsync(timeout: TimeSpan.FromSeconds(1)));
    }
}
