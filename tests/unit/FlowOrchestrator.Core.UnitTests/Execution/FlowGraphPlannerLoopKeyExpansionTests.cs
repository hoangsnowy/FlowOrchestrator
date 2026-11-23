using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Locks the runtime loop-key expansion performed by <see cref="FlowGraphPlanner.Evaluate"/>: every
/// scope instance is expanded once, each missing child is classified exactly once, and the classified
/// lists come back in ordinal order.
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
        var classified = evaluation.ReadyStepKeys
            .Concat(evaluation.BlockedStepKeys)
            .Concat(evaluation.WaitingStepKeys)
            .ToArray();
        Assert.Equal(classified.Distinct(StringComparer.Ordinal).Count(), classified.Length);
        Assert.Equal(["loop.1.child_1"], evaluation.ReadyStepKeys);
        Assert.Equal(["after"], evaluation.WaitingStepKeys);
        Assert.DoesNotContain("loop.0.child_0", classified);
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

        // Assert — the inner scope instance "outer.0.inner.0" is expanded to its missing child.
        Assert.Equal(["outer.0.inner.0.tail"], evaluation.ReadyStepKeys);
        Assert.Empty(evaluation.BlockedStepKeys);
        Assert.Empty(evaluation.WaitingStepKeys);
    }

    [Fact]
    public void Evaluate_ReadyKeys_AreOrdinalSorted()
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

        // Assert — "loop.10" sorts before "loop.2" ordinally.
        Assert.Equal(["loop.10.child_1", "loop.2.child_1"], evaluation.ReadyStepKeys);
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
            ["leaf"] = new StepMetadata { Type = "noop" },
            ["tail"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["leaf"] = [StepStatus.Succeeded] }
            }
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
