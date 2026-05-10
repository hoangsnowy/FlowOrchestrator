using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Locks the canonical terminal-status rules used by both <c>FlowOrchestratorEngine</c>
/// (graph continuation) and <c>FlowRunRecoveryHostedService</c> (zombie-run closure).
/// Both call <see cref="RunTerminationClassifier.ComputeTerminalStatus"/> as the single
/// source of truth — these tests exist so a future change to either consumer cannot
/// silently re-diverge the behaviour.
/// </summary>
public class RunTerminationClassifierTests
{
    private static IFlowDefinition FlowWith(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    [Fact]
    public void NoSucceeded_AnyFailed_ReturnsFailed()
    {
        // Arrange
        var flow = FlowWith(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["b"] = new StepMetadata { Type = "B" }
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["a"] = StepStatus.Failed,
            ["b"] = StepStatus.Skipped
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Failed.ToString(), status);
    }

    [Fact]
    public void NoSucceeded_OnlySkipped_ReturnsSkipped()
    {
        // Arrange
        var flow = FlowWith(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" }
        });
        var statuses = new Dictionary<string, StepStatus> { ["a"] = StepStatus.Skipped };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Skipped.ToString(), status);
    }

    [Fact]
    public void EmptyStatuses_ReturnsFailed()
    {
        // Arrange
        var flow = FlowWith(new StepCollection());
        var statuses = new Dictionary<string, StepStatus>();

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert — "no success and nothing else" defaults to Failed per existing semantics.
        Assert.Equal(StepStatus.Failed.ToString(), status);
    }

    [Fact]
    public void SucceededWithUnhandledFailure_ReturnsFailed()
    {
        // Arrange — `a` failed and no downstream recovery handler succeeded after it.
        var flow = FlowWith(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["b"] = new StepMetadata { Type = "B" } // independent step, not a recovery handler for 'a'
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["a"] = StepStatus.Failed,
            ["b"] = StepStatus.Succeeded
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Failed.ToString(), status);
    }

    [Fact]
    public void SucceededWithHandledFailure_ReturnsSucceeded()
    {
        // Arrange — `a` failed, `recover` runs after `a` and succeeded → recovery is explicit.
        var flow = FlowWith(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["recover"] = new StepMetadata
            {
                Type = "Recover",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Failed] }
            }
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["a"] = StepStatus.Failed,
            ["recover"] = StepStatus.Succeeded
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
    }

    [Fact]
    public void AllLeavesSkipped_ReturnsSkipped()
    {
        // Arrange — `a` succeeded, both leaves `b` and `c` skipped (When clause false).
        var flow = FlowWith(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["b"] = new StepMetadata
            {
                Type = "B",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            },
            ["c"] = new StepMetadata
            {
                Type = "C",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            }
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["a"] = StepStatus.Succeeded,
            ["b"] = StepStatus.Skipped,
            ["c"] = StepStatus.Skipped
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Skipped.ToString(), status);
    }

    [Fact]
    public void SucceededWithSomeSkippedLeaves_ReturnsSucceeded()
    {
        // Arrange — at least one leaf succeeded, so the run is Succeeded even though
        // a sibling leaf was Skipped.
        var flow = FlowWith(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["b"] = new StepMetadata
            {
                Type = "B",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            },
            ["c"] = new StepMetadata
            {
                Type = "C",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            }
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["a"] = StepStatus.Succeeded,
            ["b"] = StepStatus.Succeeded,
            ["c"] = StepStatus.Skipped
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
    }

    [Fact]
    public void SingleSucceededStep_ReturnsSucceeded()
    {
        // Arrange — trivial linear flow with one step succeeded.
        var flow = FlowWith(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" }
        });
        var statuses = new Dictionary<string, StepStatus> { ["a"] = StepStatus.Succeeded };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
    }
}
