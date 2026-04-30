using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

public class OutputsRepositoryTypedExtensionsTests
{
    private readonly InMemoryOutputsRepository _sut = new();

    [Fact]
    public async Task GetTriggerDataAsync_WithTypedResult_ReturnsDeserializedPayload()
    {
        // Arrange
        var flow = CreateFlow();
        var trigger = new Trigger("manual", "Manual", new TriggerPayload { JobId = "JOB-9", Attempt = 2 });
        var ctx = new TriggerContext { RunId = Guid.NewGuid(), Flow = flow, Trigger = trigger };
        await _sut.SaveTriggerDataAsync(ctx, flow, trigger);

        // Act
        var payload = await _sut.GetTriggerDataAsync<TriggerPayload>(ctx.RunId);

        // Assert
        Assert.NotNull(payload);
        Assert.Equal("JOB-9", payload!.JobId);
        Assert.Equal(2, payload.Attempt);
    }

    [Fact]
    public async Task GetStepOutputAsync_WithTypedResult_ReturnsDeserializedPayload()
    {
        // Arrange
        var flow = CreateFlow();
        var runId = Guid.NewGuid();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("step1", "Typed") { RunId = runId };
        var result = new StepResult
        {
            Key = step.Key,
            Result = new TriggerPayload { JobId = "JOB-22", Attempt = 7 }
        };
        await _sut.SaveStepOutputAsync(ctx, flow, step, result);

        // Act
        var payload = await _sut.GetStepOutputAsync<TriggerPayload>(runId, step.Key);

        // Assert
        Assert.NotNull(payload);
        Assert.Equal("JOB-22", payload!.JobId);
        Assert.Equal(7, payload.Attempt);
    }

    [Fact]
    public async Task GetStepOutputAsync_WithInvalidShape_ThrowsJsonException()
    {
        // Arrange
        var flow = CreateFlow();
        var runId = Guid.NewGuid();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("step1", "Typed") { RunId = runId };
        var result = new StepResult { Key = step.Key, Result = new { attempt = "bad" } };
        await _sut.SaveStepOutputAsync(ctx, flow, step, result);

        // Act
        var act = async () => await _sut.GetStepOutputAsync<TriggerPayload>(runId, step.Key);

        // Assert
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(act);
    }

    private static IFlowDefinition CreateFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());
        return flow;
    }

    private sealed class TriggerPayload
    {
        public string JobId { get; set; } = string.Empty;
        public int Attempt { get; set; }
    }
}
