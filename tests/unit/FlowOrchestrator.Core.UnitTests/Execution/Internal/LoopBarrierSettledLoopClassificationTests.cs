using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Terminal-status classification for runs whose ForEach loop was settled by the completion
/// barrier. The barrier always writes <see cref="StepStatus.Succeeded"/> for a loop step whose
/// iterations are all terminal — including when those iterations Failed or were Skipped — so the
/// classifier must keep deriving the run status from the iteration steps, never from the loop
/// step's own (synthetic) success.
/// </summary>
public sealed class LoopBarrierSettledLoopClassificationTests
{
    private static IFlowDefinition LoopFlow(StepCollection body, bool withDownstream)
    {
        var steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata { Type = "ForEach", Steps = body }
        };

        if (withDownstream)
        {
            steps["after_loop"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
            };
        }

        var flow = Substitute.For<IFlowDefinition>();
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    [Fact]
    public void SettledLoopWithFailedIteration_andSucceededDownstream_ReturnsFailed()
    {
        // Arrange - the barrier settled "loop" as Succeeded even though iteration 0 failed, and
        // "after_loop" then ran successfully. Neither of those successes is a recovery handler
        // for the failed iteration.
        var flow = LoopFlow(new StepCollection { ["work"] = new StepMetadata { Type = "Echo" } }, withDownstream: true);
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Succeeded,
            ["loop.0.work"] = StepStatus.Failed,
            ["loop.1.work"] = StepStatus.Succeeded,
            ["after_loop"] = StepStatus.Succeeded
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert - a barrier-settled loop must not mask an unhandled iteration failure.
        Assert.Equal(StepStatus.Failed.ToString(), status);
    }

    [Fact]
    public void SettledLoopWithMixedFailedAndSkippedIterations_ReturnsFailed()
    {
        // Arrange - iteration 0's head failed, which cascade-skipped its tail; the barrier still
        // settled the loop because Failed and Skipped are both terminal.
        var flow = LoopFlow(new StepCollection
        {
            ["head"] = new StepMetadata { Type = "Echo" },
            ["tail"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["head"] = [StepStatus.Succeeded] }
            }
        }, withDownstream: false);

        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Succeeded,
            ["loop.0.head"] = StepStatus.Failed,
            ["loop.0.tail"] = StepStatus.Skipped
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Failed.ToString(), status);
    }

    [Fact]
    public void SettledLoopWithZeroIterations_ReturnsSucceeded()
    {
        // Arrange - an empty collection settles the loop immediately with no iteration rows at
        // all, so the loop step is itself the leaf of the executed graph.
        var flow = LoopFlow(new StepCollection { ["work"] = new StepMetadata { Type = "Echo" } }, withDownstream: true);
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Succeeded,
            ["after_loop"] = StepStatus.Succeeded
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
    }

    [Fact]
    public void SettledLoopWithAllIterationsSkipped_andSucceededDownstream_ReturnsSucceeded()
    {
        // Arrange - every iteration was When-skipped; the downstream step is the only leaf that
        // actually produced work.
        var flow = LoopFlow(new StepCollection { ["work"] = new StepMetadata { Type = "Echo" } }, withDownstream: true);
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Succeeded,
            ["loop.0.work"] = StepStatus.Skipped,
            ["loop.1.work"] = StepStatus.Skipped,
            ["after_loop"] = StepStatus.Succeeded
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
    }
}
