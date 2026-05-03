using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.ServiceBus;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

public class StepEnvelopeRoundTripTests
{
    [Fact]
    public void From_PreservesInputsAcrossSerialization()
    {
        // Arrange
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext
        {
            RunId = Guid.NewGuid(),
            TriggerData = new { orderId = "ord-7" },
        };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var step = new StepInstance("validate", "Validate")
        {
            RunId = ctx.RunId,
            ScheduledTime = DateTimeOffset.UtcNow,
            Index = 3,
        };
        step.Inputs["amount"] = 42;
        step.Inputs["nullable"] = null;

        // Act
        var envelope = StepEnvelope.From(ctx, flow.Id, step);
        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<StepEnvelope>(json);

        // Assert
        Assert.NotNull(restored);
        Assert.Equal(envelope.FlowId, restored!.FlowId);
        Assert.Equal(envelope.RunId, restored.RunId);
        Assert.Equal(envelope.StepKey, restored.StepKey);
        Assert.Equal(envelope.StepType, restored.StepType);
        Assert.Equal(envelope.Index, restored.Index);
        Assert.NotNull(restored.Inputs);
        Assert.Equal(2, restored.Inputs!.Count);
    }

    [Fact]
    public void ToStepInstance_RestoresKeyTypeAndIndex()
    {
        // Arrange
        var envelope = new StepEnvelope
        {
            RunId = Guid.NewGuid(),
            FlowId = Guid.NewGuid(),
            StepKey = "step-x",
            StepType = "TypeX",
            ScheduledTime = DateTimeOffset.UtcNow,
            Index = 5,
        };

        // Act
        var step = envelope.ToStepInstance();

        // Assert
        Assert.Equal("step-x", step.Key);
        Assert.Equal("TypeX", step.Type);
        Assert.Equal(5, step.Index);
        Assert.Equal(envelope.RunId, step.RunId);
        Assert.Equal(envelope.ScheduledTime, step.ScheduledTime);
    }

    [Fact]
    public void ToExecutionContext_CarriesRunIdAndPrincipal()
    {
        // Arrange
        var envelope = new StepEnvelope
        {
            RunId = Guid.NewGuid(),
            PrincipalId = "u@example.com",
            TriggerHeaders = new Dictionary<string, string> { ["A"] = "B" },
        };

        // Act
        var ctx = envelope.ToExecutionContext();

        // Assert
        Assert.Equal(envelope.RunId, ctx.RunId);
        Assert.Equal("u@example.com", ctx.PrincipalId);
        Assert.NotNull(ctx.TriggerHeaders);
        Assert.Equal("B", ctx.TriggerHeaders!["A"]);
    }
}
