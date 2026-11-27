using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// <see cref="FlowTopology"/> is the reverse <c>RunAfter</c> index behind the continuation's frontier read
/// (#189 P0-1): when a step changes, the dependents it reports are the only steps the engine re-classifies,
/// so a dependent it misses is a step that never runs.
/// </summary>
public sealed class FlowTopologyTests
{
    private static IFlowDefinition Flow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private static List<string> DependentsOf(IFlowDefinition flow, string runtimeKey, out bool enumerable)
    {
        var dependents = new List<string>();
        enumerable = FlowTopology.For(flow).TryAddDependents(flow.Manifest.Steps, runtimeKey, dependents);
        dependents.Sort(StringComparer.Ordinal);
        return dependents;
    }

    private static StepCollection JoinAndLoop() => new()
    {
        ["a"] = new StepMetadata { Type = "T" },
        ["b"] = new StepMetadata { Type = "T" },
        ["join"] = new StepMetadata
        {
            Type = "T",
            RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded], ["b"] = [StepStatus.Succeeded] }
        },
        ["loop"] = new LoopStepMetadata
        {
            Type = "ForEach",
            RunAfter = new RunAfterCollection { ["join"] = [StepStatus.Succeeded] },
            Steps = new StepCollection
            {
                ["c0"] = new StepMetadata { Type = "T" },
                ["c1"] = new StepMetadata { Type = "T", RunAfter = new RunAfterCollection { ["c0"] = [StepStatus.Succeeded] } },
                ["c2"] = new StepMetadata { Type = "T", RunAfter = new RunAfterCollection { ["c0"] = [StepStatus.Succeeded] } },
                ["inner"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    RunAfter = new RunAfterCollection { ["c1"] = [StepStatus.Succeeded] },
                    Steps = new StepCollection
                    {
                        ["x"] = new StepMetadata { Type = "T" },
                        ["y"] = new StepMetadata { Type = "T", RunAfter = new RunAfterCollection { ["x"] = [StepStatus.Succeeded] } }
                    }
                }
            }
        },
        ["after"] = new StepMetadata { Type = "T", RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] } }
    };

    [Fact]
    public void TopLevelStep_reportsEveryTopLevelStepThatDependsOnIt()
    {
        // Arrange
        var flow = Flow(JoinAndLoop());

        // Act
        var ofA = DependentsOf(flow, "a", out var aEnumerable);
        var ofJoin = DependentsOf(flow, "join", out _);
        var ofLoop = DependentsOf(flow, "loop", out _);
        var ofAfter = DependentsOf(flow, "after", out var afterEnumerable);

        // Assert
        Assert.True(aEnumerable);
        Assert.Equal(["join"], ofA);
        Assert.Equal(["loop"], ofJoin);
        Assert.Equal(["after"], ofLoop);
        Assert.True(afterEnumerable);
        Assert.Empty(ofAfter);
    }

    [Fact]
    public void LoopChild_reportsItsSiblingsInTheSameIterationOnly()
    {
        // Arrange
        var flow = Flow(JoinAndLoop());

        // Act
        var dependents = DependentsOf(flow, "loop.3.c0", out var enumerable);

        // Assert - the runtime keys of iteration 3's dependents, never another iteration's.
        Assert.True(enumerable);
        Assert.Equal(["loop.3.c1", "loop.3.c2"], dependents);
    }

    [Fact]
    public void NestedLoopChild_reportsItsSiblingsInTheSameInnerIteration()
    {
        // Arrange
        var flow = Flow(JoinAndLoop());

        // Act
        var ofX = DependentsOf(flow, "loop.2.inner.5.x", out var xEnumerable);
        var ofC1 = DependentsOf(flow, "loop.2.c1", out _);
        var ofInner = DependentsOf(flow, "loop.2.inner", out var innerEnumerable);

        // Assert
        Assert.True(xEnumerable);
        Assert.Equal(["loop.2.inner.5.y"], ofX);
        Assert.Equal(["loop.2.inner"], ofC1);
        Assert.True(innerEnumerable);
        Assert.Empty(ofInner);
    }

    [Fact]
    public void LiteralDottedDependencyOfATopLevelStep_isReported()
    {
        // Arrange - a top-level step that waits on one specific iteration child by its runtime key.
        var steps = JoinAndLoop();
        steps["watch"] = new StepMetadata
        {
            Type = "T",
            RunAfter = new RunAfterCollection { ["loop.0.c0"] = [StepStatus.Succeeded] }
        };
        var flow = Flow(steps);

        // Act
        var dependents = DependentsOf(flow, "loop.0.c0", out var enumerable);

        // Assert
        Assert.True(enumerable);
        Assert.Equal(["loop.0.c1", "loop.0.c2", "watch"], dependents);
    }

    [Fact]
    public void LiteralDottedDependencyOfANestedStep_cannotBeEnumerated()
    {
        // Arrange - a body step naming a literal key exists once per iteration: no finite key list.
        var steps = JoinAndLoop();
        ((LoopStepMetadata)steps["loop"]).Steps["c3"] = new StepMetadata
        {
            Type = "T",
            RunAfter = new RunAfterCollection { ["loop.0.c0"] = [StepStatus.Succeeded] }
        };
        var flow = Flow(steps);

        // Act
        DependentsOf(flow, "loop.0.c0", out var enumerable);

        // Assert
        Assert.False(enumerable);
    }

    [Theory]
    [InlineData("__run")]
    [InlineData("ghost")]
    [InlineData("ghost.1.c0")]
    [InlineData("a.1.b")]
    public void KeyThatDoesNotResolveToAStep_cannotBeEnumerated(string runtimeKey)
    {
        // Arrange
        var flow = Flow(JoinAndLoop());

        // Act
        DependentsOf(flow, runtimeKey, out var enumerable);

        // Assert - the caller falls back to a full evaluation rather than guess.
        Assert.False(enumerable);
    }

    [Fact]
    public void EntryWhenKeyAndBlankKeys_nameNoStep()
    {
        // Arrange - "" attaches a When clause to the trigger; it must not make "a" look like a dependent.
        var flow = Flow(new StepCollection
        {
            ["a"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { [""] = new RunAfterCondition { When = "@triggerBody()?.go == true" } }
            },
            ["b"] = new StepMetadata { Type = "T", RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] } }
        });

        // Act
        var ofA = DependentsOf(flow, "a", out var enumerable);

        // Assert
        Assert.True(enumerable);
        Assert.Equal(["b"], ofA);
    }

    [Fact]
    public void For_returnsOneTopologyPerDefinition()
    {
        // Arrange
        var flow = Flow(JoinAndLoop());

        // Act
        var first = FlowTopology.For(flow);
        var second = FlowTopology.For(flow);

        // Assert - built once at first use, never per completion.
        Assert.Same(first, second);
    }
}
