using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Pins the O(n) rewrite of <see cref="RunTerminationClassifier.ComputeTerminalStatus"/> (#189) to the
/// O(n²) per-pair scan it replaced. <see cref="LegacyClassifier"/> is a verbatim copy of the previous
/// algorithm, used as an oracle: every case — hand-written loop shapes and a seeded random sweep over
/// loop, nested-loop and recovery-handler keys — must classify identically.
/// </summary>
public class RunTerminationClassifierEquivalenceTests
{
    private static readonly StepStatus[] TerminalStatuses = [StepStatus.Succeeded, StepStatus.Failed, StepStatus.Skipped];

    [Fact]
    public void LoopRun_AllSucceeded_ReturnsSucceeded()
    {
        // Arrange
        var flow = LoopFlow();
        var statuses = LoopStatuses(iterations: 50, StepStatus.Succeeded, StepStatus.Succeeded);

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
        Assert.Equal(LegacyClassifier.ComputeTerminalStatus(flow, statuses), status);
    }

    [Fact]
    public void LoopRun_BodyAndDownstreamSkipped_ReturnsSkipped()
    {
        // Arrange
        var flow = LoopFlow();
        var statuses = LoopStatuses(iterations: 50, StepStatus.Skipped, StepStatus.Skipped);

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Skipped.ToString(), status);
        Assert.Equal(LegacyClassifier.ComputeTerminalStatus(flow, statuses), status);
    }

    [Fact]
    public void LoopRun_FailedChildWithSiblingRecoveryHandler_ReturnsSucceeded()
    {
        // Arrange — "loop.3.child_1" failed; "loop.3.recover" declares RunAfter "child_1" (a bare
        // template key) and succeeded, so it must resolve to the runtime key "loop.3.child_1".
        var flow = LoopFlow();
        var statuses = LoopStatuses(iterations: 5, StepStatus.Succeeded, StepStatus.Succeeded);
        statuses["loop.3.child_1"] = StepStatus.Failed;
        statuses["loop.3.child_2"] = StepStatus.Skipped;
        statuses["loop.3.recover"] = StepStatus.Succeeded;

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Succeeded.ToString(), status);
        Assert.Equal(LegacyClassifier.ComputeTerminalStatus(flow, statuses), status);
    }

    [Fact]
    public void LoopRun_RecoveryHandlerInOtherIteration_DoesNotHandleTheFailure()
    {
        // Arrange — the handler in iteration 2 resolves to "loop.2.child_1", not "loop.3.child_1".
        var flow = LoopFlow();
        var statuses = LoopStatuses(iterations: 5, StepStatus.Succeeded, StepStatus.Succeeded);
        statuses["loop.3.child_1"] = StepStatus.Failed;
        statuses["loop.3.child_2"] = StepStatus.Skipped;
        statuses["loop.2.recover"] = StepStatus.Succeeded;

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Failed.ToString(), status);
        Assert.Equal(LegacyClassifier.ComputeTerminalStatus(flow, statuses), status);
    }

    [Fact]
    public void NestedLoopRun_SkippedLeavesUnderSucceededScopes_ReturnsSkipped()
    {
        // Arrange — "outer" and "outer.0.inner" succeeded but are structural owners of the
        // skipped leaves, so they are not leaves themselves.
        var flow = NestedLoopFlow();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["outer"] = StepStatus.Succeeded,
            ["outer.0.inner"] = StepStatus.Succeeded,
            ["outer.0.inner.0.leaf"] = StepStatus.Skipped,
            ["outer.0.inner.1.leaf"] = StepStatus.Skipped,
            ["outer.1.inner"] = StepStatus.Succeeded,
            ["outer.1.inner.0.leaf"] = StepStatus.Skipped
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Skipped.ToString(), status);
        Assert.Equal(LegacyClassifier.ComputeTerminalStatus(flow, statuses), status);
    }

    [Fact]
    public void SelfDependency_DoesNotMakeTheStepANonLeaf()
    {
        // Arrange — "b" lists itself in RunAfter. The per-pair scan skipped the key under test as
        // a candidate, so "b" stays a leaf; its Skipped status therefore decides the run.
        var flow = FlowWith(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "A" },
            ["b"] = new StepMetadata
            {
                Type = "B",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded], ["b"] = [StepStatus.Succeeded] }
            }
        });
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["a"] = StepStatus.Succeeded,
            ["b"] = StepStatus.Skipped
        };

        // Act
        var status = RunTerminationClassifier.ComputeTerminalStatus(flow, statuses);

        // Assert
        Assert.Equal(StepStatus.Skipped.ToString(), status);
        Assert.Equal(LegacyClassifier.ComputeTerminalStatus(flow, statuses), status);
    }

    [Fact]
    public void RandomStatusMaps_ClassifyIdenticallyToTheLegacyScan()
    {
        // Arrange
        var flows = new[] { LoopFlow(), NestedLoopFlow() };
        var universes = new[] { LoopKeyUniverse(iterations: 4), NestedKeyUniverse(outer: 3, inner: 3) };
        var random = new Random(189);
        var mismatches = new List<string>();

        // Act
        for (var round = 0; round < 3000; round++)
        {
            var which = round % flows.Length;
            var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal);
            foreach (var key in universes[which])
            {
                if (random.Next(3) != 0)
                {
                    // Bias towards Succeeded so the leaf and failure-handling rules are reached.
                    statuses[key] = random.Next(4) == 0
                        ? TerminalStatuses[random.Next(TerminalStatuses.Length)]
                        : StepStatus.Succeeded;
                }
            }

            var expected = LegacyClassifier.ComputeTerminalStatus(flows[which], statuses);
            var actual = RunTerminationClassifier.ComputeTerminalStatus(flows[which], statuses);
            if (expected != actual)
            {
                mismatches.Add($"{expected} != {actual}: {string.Join(", ", statuses.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
            }
        }

        // Assert
        Assert.Empty(mismatches);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static IFlowDefinition FlowWith(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    /// <summary><c>entry → loop(child_0 → child_1 → child_2, child_1 -Failed→ recover) → after</c>.</summary>
    private static IFlowDefinition LoopFlow()
    {
        var body = new StepCollection
        {
            ["child_0"] = new StepMetadata { Type = "noop" },
            ["child_1"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["child_0"] = [StepStatus.Succeeded] }
            },
            ["child_2"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["child_1"] = [StepStatus.Succeeded] }
            },
            ["recover"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["child_1"] = [StepStatus.Failed] }
            }
        };

        return FlowWith(new StepCollection
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
            },
            ["cleanup"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["loop.child_2"] = [StepStatus.Failed] }
            }
        });
    }

    /// <summary><c>outer(inner(leaf → tail)) → done</c>.</summary>
    private static IFlowDefinition NestedLoopFlow()
    {
        var innerBody = new StepCollection
        {
            ["leaf"] = new StepMetadata { Type = "noop" },
            ["tail"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["leaf"] = [StepStatus.Succeeded, StepStatus.Failed] }
            }
        };

        var outerBody = new StepCollection
        {
            ["inner"] = new LoopStepMetadata { Type = "foreach", Steps = innerBody }
        };

        return FlowWith(new StepCollection
        {
            ["outer"] = new LoopStepMetadata { Type = "foreach", Steps = outerBody },
            ["done"] = new StepMetadata
            {
                Type = "noop",
                RunAfter = new RunAfterCollection { ["outer"] = [StepStatus.Succeeded] }
            }
        });
    }

    private static Dictionary<string, StepStatus> LoopStatuses(int iterations, StepStatus childStatus, StepStatus afterStatus)
    {
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["entry"] = StepStatus.Succeeded,
            ["loop"] = StepStatus.Succeeded,
            ["after"] = afterStatus
        };

        for (var i = 0; i < iterations; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                statuses[$"loop.{i}.child_{c}"] = childStatus;
            }
        }

        return statuses;
    }

    private static List<string> LoopKeyUniverse(int iterations)
    {
        var keys = new List<string> { "entry", "loop", "after", "cleanup" };
        for (var i = 0; i < iterations; i++)
        {
            keys.AddRange([$"loop.{i}.child_0", $"loop.{i}.child_1", $"loop.{i}.child_2", $"loop.{i}.recover"]);
        }

        return keys;
    }

    private static List<string> NestedKeyUniverse(int outer, int inner)
    {
        var keys = new List<string> { "outer", "done" };
        for (var o = 0; o < outer; o++)
        {
            keys.Add($"outer.{o}.inner");
            for (var i = 0; i < inner; i++)
            {
                keys.Add($"outer.{o}.inner.{i}.leaf");
                keys.Add($"outer.{o}.inner.{i}.tail");
            }
        }

        return keys;
    }

    /// <summary>The pre-#189 O(n²) classifier, kept verbatim as the equivalence oracle.</summary>
    private static class LegacyClassifier
    {
        public static string ComputeTerminalStatus(IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
        {
            var anySucceeded = false;
            var anyFailed = false;
            var anySkipped = false;
            foreach (var s in statuses.Values)
            {
                switch (s)
                {
                    case StepStatus.Succeeded: anySucceeded = true; break;
                    case StepStatus.Failed: anyFailed = true; break;
                    case StepStatus.Skipped: anySkipped = true; break;
                }
            }

            if (!anySucceeded)
            {
                return anyFailed
                    ? StepStatus.Failed.ToString()
                    : anySkipped ? StepStatus.Skipped.ToString() : StepStatus.Failed.ToString();
            }

            if (anyFailed && statuses.Any(kvp => kvp.Value == StepStatus.Failed && !IsFailureHandled(kvp.Key, flow, statuses)))
            {
                return StepStatus.Failed.ToString();
            }

            var leafEntries = statuses.Where(kvp => IsLeaf(kvp.Key, flow, statuses)).ToList();
            return (leafEntries.Count > 0 && leafEntries.TrueForAll(kvp => kvp.Value == StepStatus.Skipped))
                ? StepStatus.Skipped.ToString()
                : StepStatus.Succeeded.ToString();
        }

        private static bool IsFailureHandled(string failedStepKey, IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
            => statuses.Any(kvp => kvp.Value == StepStatus.Succeeded && DependsOn(kvp.Key, failedStepKey, flow));

        private static bool IsLeaf(string key, IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
        {
            foreach (var candidateKey in statuses.Keys)
            {
                if (string.Equals(candidateKey, key, StringComparison.Ordinal))
                {
                    continue;
                }

                var isDescendant = candidateKey.Length > key.Length
                    && candidateKey[key.Length] == '.'
                    && candidateKey.StartsWith(key, StringComparison.Ordinal);
                if (isDescendant || DependsOn(candidateKey, key, flow))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool DependsOn(string dependentKey, string predecessorKey, IFlowDefinition flow)
        {
            var metadata = flow.Manifest.Steps.FindStep(dependentKey);
            if (metadata?.RunAfter is null || metadata.RunAfter.Count == 0)
            {
                return false;
            }

            return metadata.RunAfter.Keys.Any(dependency =>
                !string.IsNullOrEmpty(dependency)
                && string.Equals(Resolve(dependentKey, dependency), predecessorKey, StringComparison.Ordinal));
        }

        private static string Resolve(string runtimeStepKey, string dependencyKey)
        {
            if (dependencyKey.Contains('.', StringComparison.Ordinal))
            {
                return dependencyKey;
            }

            var lastDot = runtimeStepKey.LastIndexOf('.');
            return lastDot <= 0 ? dependencyKey : $"{runtimeStepKey[..lastDot]}.{dependencyKey}";
        }
    }
}
