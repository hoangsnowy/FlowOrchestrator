using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
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
        // Arrange
        var flow = CreateFlow();
        var trigger = new Trigger("manual", "Manual", new { foo = "bar" });
        var ctx = new TriggerContext { RunId = Guid.NewGuid(), Flow = flow, Trigger = trigger };

        // Act
        await _sut.SaveTriggerDataAsync(ctx, flow, trigger);
        var result = await _sut.GetTriggerDataAsync(ctx.RunId);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetTriggerDataAsync_NonExistentRunId_ReturnsNull()
    {
        // Arrange

        // Act
        var result = await _sut.GetTriggerDataAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAndGetTriggerHeaders_RoundTrip()
    {
        // Arrange
        var flow = CreateFlow();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Correlation-Id"] = "corr-123",
            ["Content-Type"] = "application/json"
        };
        var trigger = new Trigger("manual", "Manual", new { foo = "bar" }, headers);
        var ctx = new TriggerContext { RunId = Guid.NewGuid(), Flow = flow, Trigger = trigger };

        // Act
        await _sut.SaveTriggerHeadersAsync(ctx, flow, trigger);
        var result = await _sut.GetTriggerHeadersAsync(ctx.RunId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("corr-123", result!["X-Correlation-Id"]);
        Assert.Equal("application/json", result["content-type"]);
    }

    [Fact]
    public async Task SaveAndGetStepOutput_RoundTrip()
    {
        // Arrange
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };
        var stepResult = new StepResult { Key = "step1", Result = new { count = 42 } };

        // Act
        await _sut.SaveStepOutputAsync(ctx, flow, step, stepResult);
        var output = await _sut.GetStepOutputAsync(ctx.RunId, "step1");

        // Assert
        Assert.NotNull(output);
    }

    [Fact]
    public async Task GetStepOutputAsync_NonExistentKey_ReturnsNull()
    {
        // Arrange

        // Act
        var result = await _sut.GetStepOutputAsync(Guid.NewGuid(), "missing");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveStepInputAsync_StoresInput()
    {
        // Arrange
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "Query")
        {
            RunId = ctx.RunId,
            Inputs = new Dictionary<string, object?> { ["sql"] = "SELECT 1" }
        };

        // Act
        await _sut.SaveStepInputAsync(ctx, flow, step);

        // Assert
        var result = await _sut.GetStepOutputAsync(ctx.RunId, "step1:input");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task EndScopeAsync_DoesNotThrow()
    {
        // Arrange
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "A") { RunId = ctx.RunId };

        // Act
        var ex = await Record.ExceptionAsync(async () => await _sut.EndScopeAsync(ctx, flow, step));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task RecordEventAsync_DoesNotThrow()
    {
        // Arrange
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "A") { RunId = ctx.RunId };
        var evt = new FlowEvent { Type = "Info", Message = "Test event" };

        // Act
        var ex = await Record.ExceptionAsync(async () => await _sut.RecordEventAsync(ctx, flow, step, evt));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task SaveStepOutput_WithNullResult_StoresSuccessfully()
    {
        // Arrange
        var flow = CreateFlow();
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "A") { RunId = ctx.RunId };
        var stepResult = new StepResult { Key = "step1", Result = null };

        // Act
        await _sut.SaveStepOutputAsync(ctx, flow, step, stepResult);
        var output = await _sut.GetStepOutputAsync(ctx.RunId, "step1");

        // Assert
        Assert.NotNull(output);
    }
}
