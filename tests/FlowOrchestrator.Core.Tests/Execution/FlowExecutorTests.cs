using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FluentAssertions;
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

    private static ITriggerContext CreateTriggerContext(IFlowDefinition flow)
    {
        var trigger = new Trigger("manual", "Manual", new { source = "test" });
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
        var ctx = CreateTriggerContext(flow);

        var result = await _sut.TriggerFlow(ctx);

        result.Key.Should().Be("step1");
        result.Type.Should().Be("LogMessage");
        result.RunId.Should().Be(ctx.RunId);
        ctx.TriggerData.Should().BeSameAs(ctx.Trigger.Data);
        result.TriggerData.Should().BeSameAs(ctx.TriggerData);
        await _outputsRepo.Received(1).SaveTriggerDataAsync(ctx, flow, ctx.Trigger);
    }

    [Fact]
    public async Task TriggerFlow_ThrowsWhenNoEntryStep()
    {
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

        var act = () => _sut.TriggerFlow(ctx).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*entry step*");
    }

    [Fact]
    public async Task GetNextStep_ReturnsNextStepWhenRunAfterMatches()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "A" },
            ["step2"] = new StepMetadata
            {
                Type = "B",
                RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded } },
                Inputs = new Dictionary<string, object?> { ["key"] = "val" }
            }
        };
        var flow = CreateFlow(steps);
        var triggerData = new { source = "trigger" };
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = triggerData };
        var currentStep = new StepInstance("step1", "A") { RunId = ctx.RunId };
        var result = new StepResult { Key = "step1", Status = StepStatus.Succeeded };

        var next = await _sut.GetNextStep(ctx, flow, currentStep, result);

        next.Should().NotBeNull();
        next!.Key.Should().Be("step2");
        next.Type.Should().Be("B");
        next.RunId.Should().Be(ctx.RunId);
        next.TriggerData.Should().BeSameAs(triggerData);
    }

    [Fact]
    public async Task GetNextStep_ReturnsNullWhenNoSuccessor()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "A" }
        };
        var flow = CreateFlow(steps);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var currentStep = new StepInstance("step1", "A") { RunId = ctx.RunId };
        var result = new StepResult { Key = "step1", Status = StepStatus.Succeeded };

        var next = await _sut.GetNextStep(ctx, flow, currentStep, result);

        next.Should().BeNull();
    }

    [Fact]
    public async Task GetNextStep_ReturnsNullWhenStatusNotAllowed()
    {
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "A" },
            ["step2"] = new StepMetadata
            {
                Type = "B",
                RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded } }
            }
        };
        var flow = CreateFlow(steps);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var currentStep = new StepInstance("step1", "A") { RunId = ctx.RunId };
        var result = new StepResult { Key = "step1", Status = StepStatus.Failed };

        var next = await _sut.GetNextStep(ctx, flow, currentStep, result);

        next.Should().BeNull();
    }

    [Fact]
    public async Task TriggerFlow_CopiesInputsToStepInstance()
    {
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

        var instance = await _sut.TriggerFlow(ctx);

        instance.Inputs.Should().ContainKey("sql");
    }
}
