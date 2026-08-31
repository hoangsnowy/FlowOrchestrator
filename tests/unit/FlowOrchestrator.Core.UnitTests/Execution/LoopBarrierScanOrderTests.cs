using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Locks the observable contract of <see cref="LoopBarrier.AllIterationsSettled"/> against the
/// scan-order change made for performance: the predicate is a conjunction over every
/// (iteration, child) pair, so which iteration the scan visits first must not alter the answer.
/// </summary>
public class LoopBarrierScanOrderTests
{
    private const int Iterations = 6;

    [Fact]
    public void AllIterationsSettled_EveryIterationTerminal_ReturnsTrue()
    {
        // Arrange
        var flow = CreateLoopFlow();
        var statuses = BuildStatuses(outstandingIteration: -1);

        // Act
        var settled = LoopBarrier.AllIterationsSettled(flow, "loop", Iterations, statuses);

        // Assert
        Assert.True(settled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Iterations / 2)]
    [InlineData(Iterations - 2)]
    [InlineData(Iterations - 1)]
    public void AllIterationsSettled_OneIterationOutstanding_ReturnsFalseRegardlessOfPosition(int outstanding)
    {
        // Arrange
        var flow = CreateLoopFlow();
        var statuses = BuildStatuses(outstandingIteration: outstanding);

        // Act
        var settled = LoopBarrier.AllIterationsSettled(flow, "loop", Iterations, statuses);

        // Assert
        Assert.False(settled);
    }

    [Fact]
    public void AllIterationsSettled_MissingStatusRowForLastIteration_ReturnsFalse()
    {
        // Arrange — the fan-out has not reached the final iteration yet, so it has no rows at all.
        var flow = CreateLoopFlow();
        var statuses = BuildStatuses(outstandingIteration: -1);
        foreach (var childKey in new[] { "child_0", "child_1" })
        {
            statuses.Remove($"loop.{Iterations - 1}.{childKey}");
        }

        // Act
        var settled = LoopBarrier.AllIterationsSettled(flow, "loop", Iterations, statuses);

        // Assert
        Assert.False(settled);
    }

    [Fact]
    public void AllIterationsSettled_FailedAndSkippedChildren_CountAsTerminal()
    {
        // Arrange
        var flow = CreateLoopFlow();
        var statuses = BuildStatuses(outstandingIteration: -1);
        statuses["loop.0.child_0"] = StepStatus.Failed;
        statuses[$"loop.{Iterations - 1}.child_1"] = StepStatus.Skipped;

        // Act
        var settled = LoopBarrier.AllIterationsSettled(flow, "loop", Iterations, statuses);

        // Assert
        Assert.True(settled);
    }

    private static Dictionary<string, StepStatus> BuildStatuses(int outstandingIteration)
    {
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Running
        };

        for (var i = 0; i < Iterations; i++)
        {
            statuses[$"loop.{i}.child_0"] = StepStatus.Succeeded;
            statuses[$"loop.{i}.child_1"] = i == outstandingIteration
                ? StepStatus.Running
                : StepStatus.Succeeded;
        }

        return statuses;
    }

    private static IFlowDefinition CreateLoopFlow()
    {
        var body = new StepCollection
        {
            ["child_0"] = new StepMetadata { Type = "noop" },
            ["child_1"] = new StepMetadata { Type = "noop" }
        };

        var steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata { Type = "foreach", Steps = body }
        };

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }
}
