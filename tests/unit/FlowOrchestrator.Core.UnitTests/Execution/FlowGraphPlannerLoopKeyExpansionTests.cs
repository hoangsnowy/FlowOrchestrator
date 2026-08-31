using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Locks the runtime loop-key expansion performed by <c>FlowGraphPlanner.BuildKnownStepKeys</c>
/// against the allocation refactor: expanding each scope prefix once instead of once per child,
/// and short-circuiting <c>RemoveNumericSegments</c> when nothing needs removing, must leave the
/// known/ready/waiting sets byte-identical.
/// </summary>
public class FlowGraphPlannerLoopKeyExpansionTests
{
    private readonly FlowGraphPlanner _sut = new();

    [Fact]
    public void Evaluate_LoopRun_ExpandsEveryIterationChildExactlyOnce()
    {
        // Arrange
        var flow = CreateLoopFlow();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["entry"] = StepStatus.Succeeded,
            ["loop"] = StepStatus.Running,
            ["loop.0.child_0"] = StepStatus.Succeeded,
            ["loop.0.child_1"] = StepStatus.Succeeded,
            ["loop.1.child_0"] = StepStatus.Succeeded
        };

        // Act
        var evaluation = _sut.Evaluate(flow, statuses);

        // Assert
        Assert.Equal(evaluation.AllKnownStepKeys.Distinct(StringComparer.Ordinal).Count(), evaluation.AllKnownStepKeys.Count);
        Assert.Contains("loop.0.child_0", evaluation.AllKnownStepKeys);
        Assert.Contains("loop.1.child_1", evaluation.AllKnownStepKeys);
        Assert.Contains("loop.1.child_1", evaluation.ReadyStepKeys);
        Assert.DoesNotContain("after", evaluation.ReadyStepKeys);
    }

    [Fact]
    public void Evaluate_NestedLoopRun_ExpandsInnerScopeChildren()
    {
        // Arrange
        var flow = CreateNestedLoopFlow();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["outer"] = StepStatus.Running,
            ["outer.0.inner"] = StepStatus.Running,
            ["outer.0.inner.0.leaf"] = StepStatus.Succeeded
        };

        // Act
        var evaluation = _sut.Evaluate(flow, statuses);

        // Assert
        Assert.Contains("outer.0.inner", evaluation.AllKnownStepKeys);
        Assert.Contains("outer.0.inner.0.leaf", evaluation.AllKnownStepKeys);
        Assert.Equal(evaluation.AllKnownStepKeys.Distinct(StringComparer.Ordinal).Count(), evaluation.AllKnownStepKeys.Count);
    }

    [Fact]
    public void Evaluate_KnownKeys_AreOrdinalSorted()
    {
        // Arrange
        var flow = CreateLoopFlow();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["entry"] = StepStatus.Succeeded,
            ["loop"] = StepStatus.Running,
            ["loop.10.child_0"] = StepStatus.Succeeded,
            ["loop.2.child_0"] = StepStatus.Succeeded
        };

        // Act
        var evaluation = _sut.Evaluate(flow, statuses);

        // Assert
        Assert.Equal(
            evaluation.AllKnownStepKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            evaluation.AllKnownStepKeys.ToArray());
    }

    private static IFlowDefinition CreateLoopFlow()
    {
        var body = new StepCollection
        {
            ["child_0"] = new StepMetadata { Type = "noop" },
            ["child_1"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["child_0"] = [StepStatus.Succeeded] }
            }
        };

        var steps = new StepCollection
        {
            ["entry"] = new StepMetadata { Type = "noop" },
            ["loop"] = new LoopStepMetadata
            {
                Type = "foreach",
                Steps = body,
                RunAfter = new RunAfterCollection { ["entry"] = [StepStatus.Succeeded] }
            },
            ["after"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
            }
        };

        return CreateFlow(steps);
    }

    private static IFlowDefinition CreateNestedLoopFlow()
    {
        var innerBody = new StepCollection
        {
            ["leaf"] = new StepMetadata { Type = "noop" }
        };

        var outerBody = new StepCollection
        {
            ["inner"] = new LoopStepMetadata { Type = "foreach", Steps = innerBody }
        };

        var steps = new StepCollection
        {
            ["outer"] = new LoopStepMetadata { Type = "foreach", Steps = outerBody }
        };

        return CreateFlow(steps);
    }

    private static IFlowDefinition CreateFlow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }
}
