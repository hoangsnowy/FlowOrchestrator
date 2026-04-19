using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using FluentAssertions;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

public class InMemoryOutputsRepositoryTests
{
    private readonly InMemoryOutputsRepository _sut = new();

    private static IFlowDefinition CreateFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());
        return flow;
    }

    [Fact]
    public async Task SaveAndGetTriggerData_RoundTrip()
    {
        var flow = CreateFlow();
        var trigger = new Trigger("manual", "Manual", new { foo = "bar" });
        var ctx = new TriggerContext { RunId = Guid.NewGuid(), Flow = flow, Trigger = trigger };

        await _sut.SaveTriggerDataAsync(ctx, flow, trigger);
        var result = await _sut.GetTriggerDataAsync(ctx.RunId);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetTriggerDataAsync_NonExistentRunId_ReturnsNull()
    {
        var result = await _sut.GetTriggerDataAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndGetTriggerHeaders_RoundTrip()
    {
        var flow = CreateFlow();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Correlation-Id"] = "corr-123",
            ["Content-Type"] = "application/json"
        };
        var trigger = new Trigger("manual", "Manual", new { foo = "bar" }, headers);
        var ctx = new TriggerContext { RunId = Guid.NewGuid(), Flow = flow, Trigger = trigger };

        await _sut.SaveTriggerHeadersAsync(ctx, flow, trigger);
        var result = await _sut.GetTriggerHeadersAsync(ctx.RunId);

        result.Should().NotBeNull();
        result!["X-Correlation-Id"].Should().Be("corr-123");
        result["content-type"].Should().Be("application/json");
    }

    [Fact]
    public async Task SaveAndGetStepOutput_RoundTrip()
    {
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };
        var stepResult = new StepResult { Key = "step1", Result = new { count = 42 } };

        await _sut.SaveStepOutputAsync(ctx, flow, step, stepResult);
        var output = await _sut.GetStepOutputAsync(ctx.RunId, "step1");

        output.Should().NotBeNull();
    }

    [Fact]
    public async Task GetStepOutputAsync_NonExistentKey_ReturnsNull()
    {
        var result = await _sut.GetStepOutputAsync(Guid.NewGuid(), "missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveStepInputAsync_StoresInput()
    {
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "Query")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?> { ["sql"] = "SELECT 1" }
        };

        await _sut.SaveStepInputAsync(ctx, flow, step);

        var result = await _sut.GetStepOutputAsync(ctx.RunId, "step1:input");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task EndScopeAsync_DoesNotThrow()
    {
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "A") { RunId = ctx.RunId };

        var act = () => _sut.EndScopeAsync(ctx, flow, step).AsTask();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordEventAsync_DoesNotThrow()
    {
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "A") { RunId = ctx.RunId };
        var evt = new FlowEvent { Type = "Info", Message = "Test event" };

        var act = () => _sut.RecordEventAsync(ctx, flow, step, evt).AsTask();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveStepOutput_WithNullResult_StoresSuccessfully()
    {
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "A") { RunId = ctx.RunId };
        var stepResult = new StepResult { Key = "step1", Result = null };

        await _sut.SaveStepOutputAsync(ctx, flow, step, stepResult);
        var output = await _sut.GetStepOutputAsync(ctx.RunId, "step1");

        output.Should().NotBeNull();
    }
}
