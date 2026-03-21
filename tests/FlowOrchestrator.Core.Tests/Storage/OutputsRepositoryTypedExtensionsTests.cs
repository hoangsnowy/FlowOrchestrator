using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FluentAssertions;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Storage;

public class OutputsRepositoryTypedExtensionsTests
{
    private readonly InMemoryOutputsRepository _sut = new();

    [Fact]
    public async Task GetTriggerDataAsync_WithTypedResult_ReturnsDeserializedPayload()
    {
        var flow = CreateFlow();
        var trigger = new Trigger("manual", "Manual", new TriggerPayload { JobId = "JOB-9", Attempt = 2 });
        var ctx = new TriggerContext { RunId = Guid.NewGuid(), Flow = flow, Trigger = trigger };
        await _sut.SaveTriggerDataAsync(ctx, flow, trigger);

        var payload = await _sut.GetTriggerDataAsync<TriggerPayload>(ctx.RunId);

        payload.Should().NotBeNull();
        payload!.JobId.Should().Be("JOB-9");
        payload.Attempt.Should().Be(2);
    }

    [Fact]
    public async Task GetStepOutputAsync_WithTypedResult_ReturnsDeserializedPayload()
    {
        var flow = CreateFlow();
        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("step1", "Typed") { RunId = runId };
        var result = new StepResult
        {
            Key = step.Key,
            Result = new TriggerPayload { JobId = "JOB-22", Attempt = 7 }
        };
        await _sut.SaveStepOutputAsync(ctx, flow, step, result);

        var payload = await _sut.GetStepOutputAsync<TriggerPayload>(runId, step.Key);

        payload.Should().NotBeNull();
        payload!.JobId.Should().Be("JOB-22");
        payload.Attempt.Should().Be(7);
    }

    [Fact]
    public async Task GetStepOutputAsync_WithInvalidShape_ThrowsJsonException()
    {
        var flow = CreateFlow();
        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("step1", "Typed") { RunId = runId };
        var result = new StepResult { Key = step.Key, Result = new { attempt = "bad" } };
        await _sut.SaveStepOutputAsync(ctx, flow, step, result);

        var act = async () => await _sut.GetStepOutputAsync<TriggerPayload>(runId, step.Key);

        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
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
