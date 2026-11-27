using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// The frontier evaluation that replaced the per-completion full-graph evaluation (#189 P0-1). Its
/// correctness rests on two claims, both pinned here: a change to step <c>X</c> can only change the
/// classification of <c>X</c>'s dependents, and classifying those dependents from a map of just their
/// rows and their dependencies' rows gives the same verdict as <see cref="FlowGraphPlanner.Evaluate"/> on
/// the whole run.
/// </summary>
public sealed class FlowGraphPlannerFrontierTests
{
    private static readonly StepStatus?[] AnyStatus =
        [null, StepStatus.Running, StepStatus.Pending, StepStatus.Succeeded, StepStatus.Failed, StepStatus.Skipped];

    private readonly FlowGraphPlanner _sut = new();

    private static IFlowDefinition Flow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private static IFlowDefinition JoinFlow() => Flow(new StepCollection
    {
        ["a"] = new StepMetadata { Type = "T" },
        ["b"] = new StepMetadata { Type = "T" },
        ["join"] = new StepMetadata
        {
            Type = "T",
            RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded], ["b"] = [StepStatus.Succeeded] }
        }
    });

    [Fact]
    public void Join_withOnePrerequisiteDone_waits()
    {
        // Arrange
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal) { ["a"] = StepStatus.Succeeded };

        // Act
        var evaluation = _sut.EvaluateFrontier(JoinFlow(), ["join"], statuses);

        // Assert
        Assert.Empty(evaluation.ReadyStepKeys);
        Assert.Empty(evaluation.BlockedStepKeys);
        Assert.Equal(["join"], evaluation.WaitingStepKeys);
    }

    [Fact]
    public void Join_withBothPrerequisitesDone_isReady()
    {
        // Arrange
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["a"] = StepStatus.Succeeded,
            ["b"] = StepStatus.Succeeded
        };

        // Act
        var evaluation = _sut.EvaluateFrontier(JoinFlow(), ["join", "join"], statuses);

        // Assert - duplicates in the candidate list classify once.
        Assert.Equal(["join"], evaluation.ReadyStepKeys);
    }

    [Fact]
    public void Join_withAFailedPrerequisite_isBlockedEvenWhileTheOtherRuns()
    {
        // Arrange
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["a"] = StepStatus.Failed,
            ["b"] = StepStatus.Running
        };

        // Act
        var evaluation = _sut.EvaluateFrontier(JoinFlow(), ["join"], statuses);

        // Assert
        Assert.Equal(["join"], evaluation.BlockedStepKeys);
    }

    [Fact]
    public void CandidateWithARow_isNotClassified()
    {
        // Arrange
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["a"] = StepStatus.Succeeded,
            ["b"] = StepStatus.Succeeded,
            ["join"] = StepStatus.Running
        };

        // Act
        var evaluation = _sut.EvaluateFrontier(JoinFlow(), ["join"], statuses);

        // Assert
        Assert.Empty(evaluation.ReadyStepKeys);
        Assert.Empty(evaluation.WaitingStepKeys);
    }

    [Fact]
    public void LoopChild_resolvesItsDependencyInsideItsOwnIteration()
    {
        // Arrange
        var flow = Flow(RandomFlows.LoopManifest());
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop.3.b0"] = StepStatus.Succeeded,
            ["loop.4.b0"] = StepStatus.Running
        };

        // Act
        var evaluation = _sut.EvaluateFrontier(flow, ["loop.3.b1", "loop.4.b1"], statuses);

        // Assert
        Assert.Equal(["loop.3.b1"], evaluation.ReadyStepKeys);
        Assert.Equal(["loop.4.b1"], evaluation.WaitingStepKeys);
    }

    [Fact]
    public void DefaultInterfaceImplementation_matchesTheDirectClassification()
    {
        // Arrange - a custom planner that only implements Evaluate gets the filtered-Evaluate default.
        var flow = JoinFlow();
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["a"] = StepStatus.Succeeded,
            ["b"] = StepStatus.Succeeded
        };
        IFlowGraphPlanner custom = new EvaluateOnlyPlanner(_sut);

        // Act
        var viaDefault = custom.EvaluateFrontier(flow, ["join"], statuses);
        var direct = _sut.EvaluateFrontier(flow, ["join"], statuses);

        // Assert
        Assert.Equal(direct.ReadyStepKeys, viaDefault.ReadyStepKeys);
        Assert.Equal(direct.BlockedStepKeys, viaDefault.BlockedStepKeys);
        Assert.Equal(direct.WaitingStepKeys, viaDefault.WaitingStepKeys);
    }

    [Fact]
    public void RandomRuns_aChangeOnlyReclassifiesItsDependents_andTheFrontierAgreesWithTheFullEvaluation()
    {
        // Arrange - random DAGs with a loop (and a nested loop) and random status maps. For each, one
        // step's row is changed; "before" is the run without that change.
        var random = new Random(189);
        var checkedChanges = 0;

        for (var round = 0; round < 400; round++)
        {
            var flow = Flow(RandomFlows.Manifest(random));
            var after = RandomFlows.Statuses(flow, random, AnyStatus);
            if (after.Count == 0)
            {
                continue;
            }

            var changedKey = after.Keys.ElementAt(random.Next(after.Count));
            var before = new Dictionary<string, StepStatus>(after, StringComparer.Ordinal);
            before.Remove(changedKey);

            var dependents = new HashSet<string>(StringComparer.Ordinal);
            if (!FlowTopology.For(flow).TryAddDependents(flow.Manifest.Steps, changedKey, dependents))
            {
                continue;
            }

            var readKeys = new HashSet<string>(dependents, StringComparer.Ordinal);
            foreach (var dependent in dependents)
            {
                FlowGraphPlanner.AddRuntimeDependencyKeys(flow, dependent, readKeys);
            }

            var partial = after.Where(pair => readKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            // Act
            var fullBefore = _sut.Evaluate(flow, before);
            var fullAfter = _sut.Evaluate(flow, after);
            var frontier = _sut.EvaluateFrontier(flow, dependents, partial);

            // Assert - (1) a step with dependencies that became ready or blocked because of the change is a
            // dependent of it (a step with none — a loop body's entry — is ready as soon as its iteration has
            // any row, and is dispatched by loop admission, never by the frontier); (2) the frontier classifies
            // the dependents exactly as the full evaluation does.
            foreach (var key in fullAfter.ReadyStepKeys.Except(fullBefore.ReadyStepKeys)
                         .Concat(fullAfter.BlockedStepKeys.Except(fullBefore.BlockedStepKeys))
                         .Where(key => HasDependencies(flow, key)))
            {
                Assert.True(
                    dependents.Contains(key),
                    $"round {round}: '{key}' changed class after '{changedKey}' changed but is not one of its dependents.");
            }

            Assert.Equal(fullAfter.ReadyStepKeys.Where(dependents.Contains), frontier.ReadyStepKeys);
            Assert.Equal(fullAfter.BlockedStepKeys.Where(dependents.Contains), frontier.BlockedStepKeys);
            Assert.Equal(fullAfter.WaitingStepKeys.Where(dependents.Contains), frontier.WaitingStepKeys);
            checkedChanges++;
        }

        Assert.True(checkedChanges > 300, $"only {checkedChanges} random changes were checked");
    }

    private static bool HasDependencies(IFlowDefinition flow, string runtimeKey)
        => flow.Manifest.Steps.FindStep(runtimeKey)?.RunAfter.Keys.Any(key => key.Length > 0) ?? false;

    /// <summary>A planner that implements only the members a pre-2.0 custom planner had.</summary>
    private sealed class EvaluateOnlyPlanner(FlowGraphPlanner inner) : IFlowGraphPlanner
    {
        public IReadOnlyList<IStepInstance> CreateEntrySteps(ITriggerContext context) => inner.CreateEntrySteps(context);

        public FlowGraphEvaluation Evaluate(IFlowDefinition flow, IReadOnlyDictionary<string, StepStatus> statuses)
            => inner.Evaluate(flow, statuses);

        public FlowGraphValidationResult Validate(IFlowDefinition flow) => inner.Validate(flow);
    }
}

/// <summary>Random manifests and status maps for the frontier property tests.</summary>
internal static class RandomFlows
{
    /// <summary>A fixed manifest: <c>start → loop{ b0 → b1, b0 → b2, (b1, b2) → b3 }</c>.</summary>
    public static StepCollection LoopManifest() => new()
    {
        ["start"] = new StepMetadata { Type = "T" },
        ["loop"] = new LoopStepMetadata
        {
            Type = "ForEach",
            RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Succeeded] },
            Steps = new StepCollection
            {
                ["b0"] = new StepMetadata { Type = "T" },
                ["b1"] = new StepMetadata { Type = "T", RunAfter = new RunAfterCollection { ["b0"] = [StepStatus.Succeeded] } },
                ["b2"] = new StepMetadata { Type = "T", RunAfter = new RunAfterCollection { ["b0"] = [StepStatus.Succeeded, StepStatus.Failed] } },
                ["b3"] = new StepMetadata
                {
                    Type = "T",
                    RunAfter = new RunAfterCollection { ["b1"] = [StepStatus.Succeeded], ["b2"] = [] }
                }
            }
        }
    };

    /// <summary>
    /// A random DAG of top-level steps, a loop with a random body DAG, a nested loop inside it, and a step
    /// after the loop. Every dependency points to an earlier step, so the graph is acyclic.
    /// </summary>
    public static StepCollection Manifest(Random random)
    {
        var top = new StepCollection();
        var topKeys = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var key = $"t{i}";
            top[key] = new StepMetadata { Type = "T", RunAfter = RandomRunAfter(random, topKeys) };
            topKeys.Add(key);
        }

        var inner = new StepCollection();
        var innerKeys = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var key = $"i{i}";
            inner[key] = new StepMetadata { Type = "T", RunAfter = RandomRunAfter(random, innerKeys) };
            innerKeys.Add(key);
        }

        var body = new StepCollection();
        var bodyKeys = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var key = $"b{i}";
            body[key] = new StepMetadata { Type = "T", RunAfter = RandomRunAfter(random, bodyKeys) };
            bodyKeys.Add(key);
        }

        body["inner"] = new LoopStepMetadata { Type = "ForEach", RunAfter = RandomRunAfter(random, bodyKeys), Steps = inner };
        top["loop"] = new LoopStepMetadata { Type = "ForEach", RunAfter = RandomRunAfter(random, topKeys), Steps = body };
        top["after"] = new StepMetadata
        {
            Type = "T",
            RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded], [topKeys[random.Next(topKeys.Count)]] = [] }
        };

        return top;
    }

    /// <summary>A random status map over the flow's top-level steps and two iterations of each loop level.</summary>
    public static Dictionary<string, StepStatus> Statuses(IFlowDefinition flow, Random random, StepStatus?[] choices)
    {
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal);
        void Maybe(string key)
        {
            if (choices[random.Next(choices.Length)] is { } status)
            {
                statuses[key] = status;
            }
        }

        foreach (var (key, metadata) in flow.Manifest.Steps)
        {
            Maybe(key);
            if (metadata is not IScopedStep loop)
            {
                continue;
            }

            for (var index = 0; index < 2; index++)
            {
                foreach (var (childKey, childMetadata) in loop.Steps)
                {
                    var runtimeChild = $"{key}.{index}.{childKey}";
                    Maybe(runtimeChild);
                    if (childMetadata is not IScopedStep nested)
                    {
                        continue;
                    }

                    for (var nestedIndex = 0; nestedIndex < 2; nestedIndex++)
                    {
                        foreach (var grandchild in nested.Steps.Keys)
                        {
                            Maybe($"{runtimeChild}.{nestedIndex}.{grandchild}");
                        }
                    }
                }
            }
        }

        return statuses;
    }

    private static RunAfterCollection RandomRunAfter(Random random, List<string> earlier)
    {
        var runAfter = new RunAfterCollection();
        foreach (var key in earlier)
        {
            if (random.Next(3) != 0)
            {
                continue;
            }

            runAfter[key] = random.Next(4) switch
            {
                0 => [StepStatus.Succeeded],
                1 => [StepStatus.Succeeded, StepStatus.Skipped],
                2 => [StepStatus.Failed],
                _ => []
            };
        }

        return runAfter;
    }
}
