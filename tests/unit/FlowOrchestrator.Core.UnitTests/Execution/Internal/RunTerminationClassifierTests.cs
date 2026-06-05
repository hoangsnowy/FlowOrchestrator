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

    // ── Finding C4: runtime loop keys vs template RunAfter keys ───────────────
    // The loop body's runtime keys are "loop.<index>.<child>"; before the fix, leaf detection
    // compared those against the manifest's TEMPLATE RunAfter keys, so the loop step itself was
    // wrongly treated as a leaf and its Succeeded status leaked into the run's terminal status —
    // an all-skipped loop body reported Succeeded instead of Skipped.

    private static IFlowDefinition FlowWithForEach(string loopKey, StepCollection body)
    {
        var steps = new StepCollection
        {
            [loopKey] = new LoopStepMetadata { Type = "foreach", Steps = body }
        };
        return FlowWith(steps);
    }

    [Fact]
    public void ForEachLoop_AllIterationsSkipped_ReturnsSkipped()
    {
        // Arrange — loop step succeeded as a dispatcher, but every iteration of its single
        // child step was skipped. The leaves are the iteration steps, all Skipped.
        var flow = FlowWithForEach("loop", new StepCollection
        {
            ["child"] = new StepMetadata { Type = "Work" }
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop"] = StepStatus.Succeeded,
            ["loop.0.child"] = StepStatus.Skipped,
            ["loop.1.child"] = StepStatus.Skipped
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert — the loop step is not a leaf (its iterations are), so the all-skipped body wins.
        Assert.Equal(StepStatus.Skipped.ToString(), status);
    }

    [Fact]
    public void ForEachLoop_IterationsSucceeded_ReturnsSucceeded()
    {
        // Arrange — same shape, but the iterations succeeded; the run must remain Succeeded.
        var flow = FlowWithForEach("loop", new StepCollection
        {
            ["child"] = new StepMetadata { Type = "Work" }
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop"] = StepStatus.Succeeded,
            ["loop.0.child"] = StepStatus.Succeeded,
            ["loop.1.child"] = StepStatus.Succeeded
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
    }

    [Fact]
    public void ForEachLoop_SequentialBody_AllSkipped_ReturnsSkipped()
    {
        // Arrange — two-step loop body where 'second' runs after 'first' (a sibling dependency
        // expressed as the bare template key). The runtime predecessor is "loop.0.first", so the
        // dependency must be resolved into runtime space for 'first' to be recognised as non-leaf.
        var flow = FlowWithForEach("loop", new StepCollection
        {
            ["first"] = new StepMetadata { Type = "Work" },
            ["second"] = new StepMetadata
            {
                Type = "Work",
                RunAfter = new RunAfterCollection { ["first"] = [StepStatus.Succeeded] }
            }
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop"] = StepStatus.Succeeded,
            ["loop.0.first"] = StepStatus.Skipped,
            ["loop.0.second"] = StepStatus.Skipped
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert — only "loop.0.second" is a leaf and it is Skipped → the run is Skipped.
        Assert.Equal(StepStatus.Skipped.ToString(), status);
    }

    [Fact]
    public void ForEachLoop_RecoveryHandlerInsideBody_HandlesIterationFailure()
    {
        // Arrange — inside the loop body, 'recover' runs after a failed 'work' and succeeds.
        // The failed iteration step ("loop.0.work") must be matched to its recovery handler via
        // runtime-key resolution, otherwise the unhandled-failure rule would force Failed.
        var flow = FlowWithForEach("loop", new StepCollection
        {
            ["work"] = new StepMetadata { Type = "Work" },
            ["recover"] = new StepMetadata
            {
                Type = "Recover",
                RunAfter = new RunAfterCollection { ["work"] = [StepStatus.Failed] }
            }
        });
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop"] = StepStatus.Succeeded,
            ["loop.0.work"] = StepStatus.Failed,
            ["loop.0.recover"] = StepStatus.Succeeded
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert — the in-body failure is handled, and a leaf ("loop.0.recover") succeeded.
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
    }
}
