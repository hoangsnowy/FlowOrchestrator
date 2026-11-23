using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

public class FlowExecutorTests
{
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly FlowExecutor _sut;

    public FlowExecutorTests()
    {
        _sut = new FlowExecutor(_outputsRepo);
    }

    private static IFlowDefinition CreateFlow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private static ITriggerContext CreateTriggerContext(IFlowDefinition flow, IReadOnlyDictionary<string, string>? headers = null)
    {
        var trigger = new Trigger("manual", "Manual", new { source = "test" }, headers);
        return new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = trigger
        };
    }

    [Fact]
    public async Task TriggerFlow_FindsEntryStep_WithNoRunAfter()
    {
        // Arrange
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Correlation-Id"] = "corr-1"
        };
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "LogMessage", RunAfter = new RunAfterCollection() },
            ["step2"] = new StepMetadata
            {
                Type = "Save",
                RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded } }
            }
        };
        var flow = CreateFlow(steps);
        var ctx = CreateTriggerContext(flow, headers);

        // Act
        var result = await _sut.TriggerFlow(ctx);

        // Assert
        Assert.Equal("step1", result.Key);
        Assert.Equal("LogMessage", result.Type);
        Assert.Equal(ctx.RunId, result.RunId);
        Assert.Same(ctx.Trigger.Data, ctx.TriggerData);
        Assert.Same(headers, ctx.TriggerHeaders);
        Assert.Same(ctx.TriggerData, result.TriggerData);
        Assert.Same(headers, result.TriggerHeaders);
        await _outputsRepo.Received(1).SaveTriggerDataAsync(ctx, flow, ctx.Trigger);
        await _outputsRepo.Received(1).SaveTriggerHeadersAsync(ctx, flow, ctx.Trigger);
    }

    [Fact]
    public async Task TriggerFlow_ThrowsWhenNoEntryStep()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata
            {
                Type = "A",
                RunAfter = new RunAfterCollection { ["step0"] = new[] { StepStatus.Succeeded } }
            }
        };
        var flow = CreateFlow(steps);
        var ctx = CreateTriggerContext(flow);

        // Act
        var act = () => _sut.TriggerFlow(ctx).AsTask();

        // Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("entry step", ex.Message);
    }

    [Fact]
    public async Task TriggerFlow_CopiesInputsToStepInstance()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata
            {
                Type = "Query",
                Inputs = new Dictionary<string, object?> { ["sql"] = "SELECT 1" }
            }
        };
        var flow = CreateFlow(steps);
        var ctx = CreateTriggerContext(flow);

        // Act
        var instance = await _sut.TriggerFlow(ctx);

        // Assert
        Assert.True(instance.Inputs.ContainsKey("sql"));
    }
}
