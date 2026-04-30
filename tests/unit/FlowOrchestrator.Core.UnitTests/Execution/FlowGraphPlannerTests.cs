using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

public class FlowGraphPlannerTests
{
    private readonly FlowGraphPlanner _sut = new();

    [Fact]
    public void Evaluate_FanOut_ReturnsMultipleReadySteps()
    {
        // Arrange
        var flow = CreateFlow(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["b"] = new StepMetadata { Type = "B", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } },
            ["c"] = new StepMetadata { Type = "C", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } }
        });

        // Act
        var evaluation = _sut.Evaluate(flow, new Dictionary<string, StepStatus> { ["a"] = StepStatus.Succeeded });

        // Assert
        Assert.Contains("b", evaluation.ReadyStepKeys);
        Assert.Contains("c", evaluation.ReadyStepKeys);
    }

    [Fact]
    public void Evaluate_FanInBlocked_ReturnsBlockedStep()
    {
        // Arrange
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

        // Act
        var evaluation = _sut.Evaluate(flow, new Dictionary<string, StepStatus>
        {
            ["a"] = StepStatus.Succeeded,
            ["b"] = StepStatus.Failed,
            ["c"] = StepStatus.Succeeded
        });

        // Assert
        Assert.Contains("d", evaluation.BlockedStepKeys);
    }

    [Fact]
    public void Evaluate_RuntimeScopedDependency_ResolvesRelativeDependency()
    {
        // Arrange
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

        // Act
        var evaluation = _sut.Evaluate(flow, new Dictionary<string, StepStatus>
        {
            ["loop.0.child1"] = StepStatus.Succeeded
        });

        // Assert
        Assert.Contains("loop.0.child2", evaluation.ReadyStepKeys);
    }

    [Fact]
    public void Validate_DetectsCycle()
    {
        // Arrange
        var flow = CreateFlow(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A", RunAfter = new RunAfterCollection { ["b"] = [StepStatus.Succeeded] } },
            ["b"] = new StepMetadata { Type = "B", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } }
        });

        // Act
        var result = _sut.Validate(flow);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    private static IFlowDefinition CreateFlow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }
}
