using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.ServiceBus;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Verifies the shim between Service Bus messages and the runtime-neutral engine. The shim's
/// only responsibilities are: rehydrate the flow from the repository (or throw), inject the
/// SB MessageId into the execution context as JobId, and forward to the engine.
/// </summary>
public class ServiceBusFlowOrchestratorTests
{
    [Fact]
    public async Task RunStepAsync_FlowNotFound_Throws()
    {
        // Arrange
        var engine = Substitute.For<IFlowOrchestrator>();
        var repo = Substitute.For<IFlowRepository>();
        repo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(Array.Empty<IFlowDefinition>()));
        var sut = new ServiceBusFlowOrchestrator(engine, repo);
        var envelope = new StepEnvelope { FlowId = Guid.NewGuid(), StepKey = "s" };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.RunStepAsync(envelope, message: null!, ct: default));
    }

    [Fact]
    public async Task TriggerByScheduleAsync_ForwardsArgsToEngine()
    {
        // Arrange
        var engine = Substitute.For<IFlowOrchestrator>();
        var repo = Substitute.For<IFlowRepository>();
        var sut = new ServiceBusFlowOrchestrator(engine, repo);
        var flowId = Guid.NewGuid();

        // Act
        await sut.TriggerByScheduleAsync(flowId, "schedule", "msg-123", ct: default);

        // Assert
        await engine.Received(1).TriggerByScheduleAsync(flowId, "schedule", "msg-123", Arg.Any<CancellationToken>());
    }
}
