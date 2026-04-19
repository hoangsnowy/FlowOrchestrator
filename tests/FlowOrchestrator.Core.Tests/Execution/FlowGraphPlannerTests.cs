using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FluentAssertions;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

public class FlowGraphPlannerTests
{
    private readonly FlowGraphPlanner _sut = new();

    [Fact]
    public void Evaluate_FanOut_ReturnsMultipleReadySteps()
    {
        var flow = CreateFlow(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["b"] = new StepMetadata { Type = "B", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } },
            ["c"] = new StepMetadata { Type = "C", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } }
        });

        var evaluation = _sut.Evaluate(flow, new Dictionary<string, StepStatus> { ["a"] = StepStatus.Succeeded });

        evaluation.ReadyStepKeys.Should().Contain(["b", "c"]);
    }

    [Fact]
    public void Evaluate_FanInBlocked_ReturnsBlockedStep()
    {
        var flow = CreateFlow(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["b"] = new StepMetadata { Type = "B", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } },
            ["c"] = new StepMetadata { Type = "C", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } },
            ["d"] = new StepMetadata
            {
                Type = "D",
                RunAfter = new RunAfterCollection
                {
                    ["b"] = [StepStatus.Succeeded],
                    ["c"] = [StepStatus.Succeeded]
                }
            }
        });

        var evaluation = _sut.Evaluate(flow, new Dictionary<string, StepStatus>
        {
            ["a"] = StepStatus.Succeeded,
            ["b"] = StepStatus.Failed,
            ["c"] = StepStatus.Succeeded
        });

        evaluation.BlockedStepKeys.Should().Contain("d");
    }

    [Fact]
    public void Evaluate_RuntimeScopedDependency_ResolvesRelativeDependency()
    {
        var flow = CreateFlow(new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                Steps = new StepCollection
                {
                    ["child1"] = new StepMetadata { Type = "S1" },
                    ["child2"] = new StepMetadata
                    {
                        Type = "S2",
                        RunAfter = new RunAfterCollection
                        {
                            ["child1"] = [StepStatus.Succeeded]
                        }
                    }
                }
            }
        });

        var evaluation = _sut.Evaluate(flow, new Dictionary<string, StepStatus>
        {
            ["loop.0.child1"] = StepStatus.Succeeded
        });

        evaluation.ReadyStepKeys.Should().Contain("loop.0.child2");
    }

    [Fact]
    public void Validate_DetectsCycle()
    {
        var flow = CreateFlow(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A", RunAfter = new RunAfterCollection { ["b"] = [StepStatus.Succeeded] } },
            ["b"] = new StepMetadata { Type = "B", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } }
        });

        var result = _sut.Validate(flow);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    private static IFlowDefinition CreateFlow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }
}
