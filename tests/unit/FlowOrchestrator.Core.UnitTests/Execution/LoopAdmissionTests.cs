using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Covers the <c>ForEach</c> concurrency gate from issue #181: <c>ConcurrencyLimit</c> must bound
/// how many iterations are in flight, not merely stagger their dispatch.
/// </summary>
public class LoopAdmissionTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>Sequential body: <c>start → wait → done</c>, the shape reported in issue #181.</summary>
    private static LoopStepMetadata SequentialBody(int concurrencyLimit = 1) => new()
    {
        Type = "ForEach",
        ConcurrencyLimit = concurrencyLimit,
        ForEach = new List<object?> { "a", "b", "c" },
        Steps = new StepCollection
        {
            ["start"] = new StepMetadata { Type = "Start", Inputs = new Dictionary<string, object?>() },
            ["wait"] = new StepMetadata
            {
                Type = "WaitForSignal",
                RunAfter = new RunAfterCollection { { "start", [StepStatus.Succeeded] } },
                Inputs = new Dictionary<string, object?>()
            },
            ["done"] = new StepMetadata
            {
                Type = "Done",
                RunAfter = new RunAfterCollection { { "wait", [StepStatus.Succeeded] } },
                Inputs = new Dictionary<string, object?>()
            }
        }
    };

    private static Dictionary<string, StepStatus> SettledIteration(string loopKey, int index) => new(StringComparer.Ordinal)
    {
        [$"{loopKey}.{index}.start"] = StepStatus.Succeeded,
        [$"{loopKey}.{index}.wait"] = StepStatus.Succeeded,
        [$"{loopKey}.{index}.done"] = StepStatus.Succeeded
    };

    private static IReadOnlySet<string> NoDispatches() => new HashSet<string>(StringComparer.Ordinal);

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NextAdmissions_SequentialLimit_AdmitsOnlyTheFirstIterationUpFront()
    {
        // Arrange
        var loop = SequentialBody();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal);

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, NoDispatches());

        // Assert
        var only = Assert.Single(admissions);
        Assert.Equal(0, only.Index);
        Assert.Equal("loop.0.start", only.RuntimeStepKey);
    }

    [Fact]
    public void NextAdmissions_IterationStillRunning_AdmitsNothing()
    {
        // Arrange — iteration 0 is parked on its WaitForSignal, which is the exact state in which
        // the pre-fix implementation had already dispatched iterations 1 and 2.
        var loop = SequentialBody();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop.0.start"] = StepStatus.Succeeded,
            ["loop.0.wait"] = StepStatus.Pending
        };

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, NoDispatches());

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public void NextAdmissions_IterationSettled_AdmitsExactlyTheNextOne()
    {
        // Arrange
        var loop = SequentialBody();
        var statuses = SettledIteration("loop", 0);

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, NoDispatches());

        // Assert
        var only = Assert.Single(admissions);
        Assert.Equal(1, only.Index);
        Assert.Equal("loop.1.start", only.RuntimeStepKey);
    }

    [Fact]
    public void NextAdmissions_DispatchedButNotYetStarted_IsNotAdmittedTwice()
    {
        // Arrange — iteration 1 was admitted by another worker and holds a ledger row, but its
        // handler has not run yet so it has no status row. Counting only statuses would over-admit.
        var loop = SequentialBody();
        var statuses = SettledIteration("loop", 0);
        var dispatched = new HashSet<string>(StringComparer.Ordinal) { "loop.1.start" };

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, dispatched);

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public void NextAdmissions_PendingPollReleasedItsLedgerRow_IsNotReadmitted()
    {
        // Arrange — a Pending poll releases its dispatch row before rescheduling, so the ledger
        // alone would report iteration 1 as never started.
        var loop = SequentialBody();
        var statuses = SettledIteration("loop", 0);
        statuses["loop.1.start"] = StepStatus.Pending;

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, NoDispatches());

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public void NextAdmissions_FailedIteration_StillReleasesItsSlot()
    {
        // Arrange — a failed child leaves its dependents Skipped by the blocked-step pass; all
        // three children are terminal, so the iteration is settled and must not wedge the loop.
        var loop = SequentialBody();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop.0.start"] = StepStatus.Failed,
            ["loop.0.wait"] = StepStatus.Skipped,
            ["loop.0.done"] = StepStatus.Skipped
        };

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, NoDispatches());

        // Assert
        Assert.Equal(1, Assert.Single(admissions).Index);
    }

    [Fact]
    public void NextAdmissions_ConcurrencyLimitTwo_KeepsTwoIterationsInFlight()
    {
        // Arrange — iteration 0 settled, iteration 1 still parked; one slot is free.
        var loop = SequentialBody(concurrencyLimit: 2);
        var statuses = SettledIteration("loop", 0);
        statuses["loop.1.start"] = StepStatus.Succeeded;
        statuses["loop.1.wait"] = StepStatus.Pending;

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, NoDispatches());

        // Assert
        Assert.Equal(2, Assert.Single(admissions).Index);
    }

    [Fact]
    public void NextAdmissions_ConcurrencyLimitTwoBothSlotsBusy_AdmitsNothing()
    {
        // Arrange
        var loop = SequentialBody(concurrencyLimit: 2);
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop.0.start"] = StepStatus.Succeeded,
            ["loop.0.wait"] = StepStatus.Pending,
            ["loop.1.start"] = StepStatus.Succeeded,
            ["loop.1.wait"] = StepStatus.Pending
        };

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, NoDispatches());

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public void NextAdmissions_NonAdjacentActiveIteration_IsStillCountedAgainstTheLimit()
    {
        // Arrange — with a limit of 2, iteration 0 can still be running while 1 has settled and 2
        // was admitted. A gate that only inspected the last `limit` iterations would miss 0 and
        // over-admit. Started iterations are 0..2, active are {0, 2}.
        var loop = SequentialBody(concurrencyLimit: 2);
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop.0.start"] = StepStatus.Succeeded,
            ["loop.0.wait"] = StepStatus.Pending,
            ["loop.2.start"] = StepStatus.Succeeded,
            ["loop.2.wait"] = StepStatus.Pending
        };
        foreach (var (key, status) in SettledIteration("loop", 1))
        {
            statuses[key] = status;
        }

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 4, statuses, NoDispatches());

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public void NextAdmissions_AllIterationsStarted_AdmitsNothing()
    {
        // Arrange
        var loop = SequentialBody();
        var statuses = SettledIteration("loop", 0);
        foreach (var index in new[] { 1, 2 })
        {
            foreach (var (key, status) in SettledIteration("loop", index))
            {
                statuses[key] = status;
            }
        }

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "loop", 3, statuses, NoDispatches());

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public void NextAdmissions_MultipleEntrySteps_AdmitsBothInTheSameIteration()
    {
        // Arrange — a fan-out body: two independent heads belong to one iteration and one slot.
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ConcurrencyLimit = 1,
            ForEach = new List<object?> { "a", "b" },
            Steps = new StepCollection
            {
                ["left"] = new StepMetadata { Type = "Left", Inputs = new Dictionary<string, object?>() },
                ["right"] = new StepMetadata { Type = "Right", Inputs = new Dictionary<string, object?>() }
            }
        };

        // Act
        var admissions = LoopAdmission.NextAdmissions(
            loop, "loop", 2, new Dictionary<string, StepStatus>(StringComparer.Ordinal), NoDispatches());

        // Assert
        Assert.Equal(2, admissions.Count);
        Assert.All(admissions, a => Assert.Equal(0, a.Index));
        Assert.Equal(["loop.0.left", "loop.0.right"], admissions.Select(a => a.RuntimeStepKey));
    }

    [Fact]
    public void NextAdmissions_NestedLoopRuntimeKey_ScopesToTheInnerLoopInstance()
    {
        // Arrange — the inner loop of outer iteration 1 has its own independent gate.
        var loop = SequentialBody();
        var statuses = SettledIteration("outer.1.inner", 0);

        // Act
        var admissions = LoopAdmission.NextAdmissions(loop, "outer.1.inner", 2, statuses, NoDispatches());

        // Assert
        Assert.Equal("outer.1.inner.1.start", Assert.Single(admissions).RuntimeStepKey);
    }

    [Fact]
    public void NextAdmissions_ZeroIterations_AdmitsNothing()
    {
        // Arrange
        var loop = SequentialBody();

        // Act
        var admissions = LoopAdmission.NextAdmissions(
            loop, "loop", 0, new Dictionary<string, StepStatus>(StringComparer.Ordinal), NoDispatches());

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public void NextAdmissions_NonScopedMetadata_AdmitsNothing()
    {
        // Arrange
        var plain = new StepMetadata { Type = "DoWork" };

        // Act
        var admissions = LoopAdmission.NextAdmissions(
            plain, "step", 3, new Dictionary<string, StepStatus>(StringComparer.Ordinal), NoDispatches());

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public void ConcurrencyLimitOf_NonPositiveLimit_ClampsToOne()
    {
        // Arrange
        var loop = new LoopStepMetadata { Type = "ForEach", ConcurrencyLimit = 0 };

        // Act
        var limit = LoopAdmission.ConcurrencyLimitOf(loop);

        // Assert
        Assert.Equal(1, limit);
    }

    [Fact]
    public void EntrySteps_FullyChainedBody_FallsBackToTheFirstDeclaredChild()
    {
        // Arrange — every child declares a RunAfter, so there is no natural head.
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection
            {
                ["first"] = new StepMetadata
                {
                    Type = "First",
                    RunAfter = new RunAfterCollection { { "outside", [StepStatus.Succeeded] } }
                },
                ["second"] = new StepMetadata
                {
                    Type = "Second",
                    RunAfter = new RunAfterCollection { { "first", [StepStatus.Succeeded] } }
                }
            }
        };

        // Act
        var entries = LoopAdmission.EntrySteps(loop);

        // Assert
        Assert.Equal("first", Assert.Single(entries).Key);
    }

    [Fact]
    public void IsIterationSettled_MissingChildRow_IsNotSettled()
    {
        // Arrange
        var loop = SequentialBody();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop.0.start"] = StepStatus.Succeeded,
            ["loop.0.wait"] = StepStatus.Succeeded
        };

        // Act
        var settled = LoopAdmission.IsIterationSettled(loop, "loop", 0, statuses);

        // Assert
        Assert.False(settled);
    }
}
