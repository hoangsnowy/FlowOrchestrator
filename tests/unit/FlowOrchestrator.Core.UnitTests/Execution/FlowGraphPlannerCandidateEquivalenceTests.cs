using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Pins <see cref="FlowGraphPlanner.Evaluate"/> after #189 removed the per-call <see cref="SortedSet{T}"/>
/// rebuild of every known runtime key. <see cref="LegacyPlanner"/> is a verbatim copy of the previous
/// eager algorithm and serves as the oracle: ready / blocked / waiting lists must match it element for
/// element, in order, for loop keys such as <c>"loop.3.child"</c>, nested loop keys, and runtime keys with
/// irregular segments. Manifests with dotted or empty top-level keys are rejected by
/// <see cref="FlowGraphPlanner.Validate"/> and are not part of the oracle's domain.
/// </summary>
public class FlowGraphPlannerCandidateEquivalenceTests
{
    private static readonly StepStatus[] Statuses =
        [StepStatus.Succeeded, StepStatus.Failed, StepStatus.Skipped, StepStatus.Running, StepStatus.Pending];

    private readonly FlowGraphPlanner _sut = new();

    [Fact]
    public void Evaluate_MidRunLoop_ReadiesOnlyTheNextChildOfTheOpenIteration()
    {
        // Arrange
        var flow = LoopFlow();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["entry"] = StepStatus.Succeeded,
            ["loop"] = StepStatus.Running,
            ["loop.0.child_0"] = StepStatus.Succeeded,
            ["loop.0.child_1"] = StepStatus.Succeeded,
            ["loop.0.child_2"] = StepStatus.Succeeded,
            ["loop.3.child_0"] = StepStatus.Succeeded,
            ["loop.3.child_1"] = StepStatus.Failed
        };

        // Act
        var evaluation = _sut.Evaluate(flow, statuses);

        // Assert
        Assert.Equal(["loop.3.recover"], evaluation.ReadyStepKeys);
        Assert.Equal(["loop.0.recover", "loop.3.child_2"], evaluation.BlockedStepKeys);
        Assert.Equal(["after"], evaluation.WaitingStepKeys);
        AssertMatchesLegacy(flow, statuses, evaluation);
    }

    [Fact]
    public void Evaluate_NestedLoop_ExpandsEveryScopeInstanceOnce()
    {
        // Arrange
        var flow = NestedLoopFlow();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["outer"] = StepStatus.Running,
            ["outer.0.inner"] = StepStatus.Running,
            ["outer.0.inner.0.leaf"] = StepStatus.Succeeded,
            ["outer.0.inner.1.leaf"] = StepStatus.Running,
            ["outer.1.inner"] = StepStatus.Running
        };

        // Act
        var evaluation = _sut.Evaluate(flow, statuses);

        // Assert
        Assert.Contains("outer.0.inner.0.tail", evaluation.ReadyStepKeys);
        AssertMatchesLegacy(flow, statuses, evaluation);
    }

    [Fact]
    public void Evaluate_IrregularRuntimeKeySegments_MatchTheLegacyNormalisation()
    {
        // Arrange — whitespace-padded and empty segments go through the original Split path.
        var flow = LoopFlow();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["entry"] = StepStatus.Succeeded,
            ["loop"] = StepStatus.Running,
            ["loop. 4 .child_0"] = StepStatus.Succeeded,
            ["loop..5.child_0"] = StepStatus.Succeeded,
            ["loop.6.child_0"] = StepStatus.Succeeded
        };

        // Act
        var evaluation = _sut.Evaluate(flow, statuses);

        // Assert
        Assert.Contains("loop.4.child_0", evaluation.ReadyStepKeys);
        AssertMatchesLegacy(flow, statuses, evaluation);
    }

    [Fact]
    public void Evaluate_RandomLoopStatusMaps_MatchTheLegacyPlanner()
    {
        // Arrange
        var flows = new[] { LoopFlow(), NestedLoopFlow() };
        var universes = new[] { LoopKeyUniverse(iterations: 5), NestedKeyUniverse(outer: 3, inner: 3) };
        var random = new Random(189);
        var mismatches = new List<string>();

        // Act
        for (var round = 0; round < 2000; round++)
        {
            var which = round % flows.Length;
            var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal);
            foreach (var key in universes[which])
            {
                if (random.Next(2) == 0)
                {
                    statuses[key] = Statuses[random.Next(Statuses.Length)];
                }
            }

            var expected = LegacyPlanner.Evaluate(flows[which], statuses);
            var actual = _sut.Evaluate(flows[which], statuses);
            if (!expected.Ready.SequenceEqual(actual.ReadyStepKeys)
                || !expected.Blocked.SequenceEqual(actual.BlockedStepKeys)
                || !expected.Waiting.SequenceEqual(actual.WaitingStepKeys))
            {
                mismatches.Add(string.Join(", ", statuses.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            }
        }

        // Assert
        Assert.Empty(mismatches);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static void AssertMatchesLegacy(IFlowDefinition flow, Dictionary<string, StepStatus> statuses, FlowGraphEvaluation actual)
    {
        var expected = LegacyPlanner.Evaluate(flow, statuses);
        Assert.Equal(expected.Ready, actual.ReadyStepKeys);
        Assert.Equal(expected.Blocked, actual.BlockedStepKeys);
        Assert.Equal(expected.Waiting, actual.WaitingStepKeys);
    }

    private static IFlowDefinition FlowWith(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private static IFlowDefinition LoopFlow() => FlowWith(LoopSteps());

    /// <summary><c>entry → loop(child_0 → child_1 → child_2, child_1 -Failed→ recover) → after</c>.</summary>
    private static StepCollection LoopSteps()
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

        return new StepCollection
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
                RunAfter = new RunAfterCollection { ["leaf"] = [StepStatus.Succeeded] }
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

    private static List<string> LoopKeyUniverse(int iterations)
    {
        var keys = new List<string> { "entry", "loop", "after" };
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

    /// <summary>The pre-#189 eager planner (SortedSet of every known key per call), kept as the oracle.</summary>
    private static class LegacyPlanner
    {
        public static (List<string> Ready, List<string> Blocked, List<string> Waiting) Evaluate(
            IFlowDefinition flow,
            IReadOnlyDictionary<string, StepStatus> statuses)
        {
            var known = BuildKnownStepKeys(flow, statuses.Keys);
            var ready = new List<string>();
            var blocked = new List<string>();
            var waiting = new List<string>();

            foreach (var stepKey in known)
            {
                if (statuses.ContainsKey(stepKey))
                {
                    continue;
                }

                var metadata = flow.Manifest.Steps.FindStep(stepKey) ?? flow.Manifest.Steps.FindStep(RemoveNumericSegments(stepKey));
                if (metadata is null)
                {
                    continue;
                }

                if (metadata.RunAfter.Count == 0)
                {
                    ready.Add(stepKey);
                    continue;
                }

                var allSatisfied = true;
                var hasFinalMismatch = false;
                foreach (var dependency in metadata.RunAfter)
                {
                    if (string.IsNullOrEmpty(dependency.Key))
                    {
                        continue;
                    }

                    if (!statuses.TryGetValue(ResolveRuntimeDependencyKey(stepKey, dependency.Key), out var dependencyStatus))
                    {
                        allSatisfied = false;
                        continue;
                    }

                    if (dependency.Value.AcceptsStatus(dependencyStatus))
                    {
                        continue;
                    }

                    allSatisfied = false;
                    if (dependencyStatus is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped)
                    {
                        hasFinalMismatch = true;
                    }
                }

                if (allSatisfied)
                {
                    ready.Add(stepKey);
                }
                else if (hasFinalMismatch)
                {
                    blocked.Add(stepKey);
                }
                else
                {
                    waiting.Add(stepKey);
                }
            }

            return (ready, blocked, waiting);
        }

        private static string[] BuildKnownStepKeys(IFlowDefinition flow, IEnumerable<string> runtimeStepKeys)
        {
            var known = new SortedSet<string>(flow.Manifest.Steps.Keys, StringComparer.Ordinal);
            foreach (var runtimeKey in runtimeStepKeys)
            {
                known.Add(runtimeKey);
                foreach (var (scopePrefix, scoped) in ExtractRuntimeScopePrefixes(flow, runtimeKey))
                {
                    foreach (var child in scoped.Steps.Keys)
                    {
                        known.Add($"{scopePrefix}.{child}");
                    }
                }
            }

            return known.ToArray();
        }

        private static IEnumerable<(string, IScopedStep)> ExtractRuntimeScopePrefixes(IFlowDefinition flow, string runtimeStepKey)
        {
            var segments = runtimeStepKey.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!int.TryParse(segments[i + 1], out _))
                {
                    continue;
                }

                var templatePath = RemoveNumericSegments(string.Join('.', segments.Take(i + 1)));
                if (flow.Manifest.Steps.FindStep(templatePath) is IScopedStep scoped)
                {
                    yield return (string.Join('.', segments.Take(i + 2)), scoped);
                }
            }
        }

        private static string ResolveRuntimeDependencyKey(string runtimeStepKey, string dependencyKey)
        {
            if (string.IsNullOrWhiteSpace(dependencyKey) || dependencyKey.Contains('.', StringComparison.Ordinal))
            {
                return dependencyKey;
            }

            var lastDot = runtimeStepKey.LastIndexOf('.');
            return lastDot <= 0 ? dependencyKey : $"{runtimeStepKey[..lastDot]}.{dependencyKey}";
        }

        private static string RemoveNumericSegments(string key)
            => string.Join('.', key
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(segment => !int.TryParse(segment, out _)));
    }
}
