using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Pins <see cref="LoopAdmission.NextAdmissionsAsync"/> — the engine's ledger-by-point-lookup gate
/// introduced for #189 — to <see cref="LoopAdmission.NextAdmissions"/>, which reads a pre-fetched set
/// of every dispatched key. Both must admit exactly the same <c>"loop.{index}.{child}"</c> keys for the
/// same ledger state, and the point-lookup form must stay cheap in the sequential steady state.
/// </summary>
public class LoopAdmissionPointLookupTests
{
    private const string LoopKey = "loop";

    [Fact]
    public async Task SequentialSteadyState_AdmitsTheNextIteration_WithOneLedgerLookup()
    {
        // Arrange — iterations 0..49 settled, nothing dispatched-but-unstarted.
        var loop = Body(concurrencyLimit: 1, entryChildren: 1);
        var statuses = Settled(loop, settledIterations: 50);
        var lookups = new List<string>();

        // Act
        var admissions = await LoopAdmission.NextAdmissionsAsync(loop, LoopKey, 100, statuses, key =>
        {
            lookups.Add(key);
            return Task.FromResult(false);
        });

        // Assert
        var admission = Assert.Single(admissions);
        Assert.Equal("loop.50.start", admission.RuntimeStepKey);
        Assert.Equal(["loop.50.start"], lookups);
    }

    [Fact]
    public async Task DispatchedButNotStartedIteration_IsNotAdmittedTwice()
    {
        // Arrange — iteration 3 holds a ledger row but no status row yet; the limit is saturated.
        var loop = Body(concurrencyLimit: 1, entryChildren: 1);
        var statuses = Settled(loop, settledIterations: 3);
        var ledger = new HashSet<string>(StringComparer.Ordinal) { "loop.3.start" };

        // Act
        var admissions = await LoopAdmission.NextAdmissionsAsync(
            loop, LoopKey, 10, statuses, key => Task.FromResult(ledger.Contains(key)));

        // Assert
        Assert.Empty(admissions);
    }

    [Fact]
    public async Task NestedLoopRuntimeKey_ProbesTheInnerLoopInstanceKeys()
    {
        // Arrange
        var loop = Body(concurrencyLimit: 2, entryChildren: 1);
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["outer.1.inner.0.start"] = StepStatus.Succeeded,
            ["outer.1.inner.0.done"] = StepStatus.Succeeded
        };
        var lookups = new List<string>();

        // Act
        var admissions = await LoopAdmission.NextAdmissionsAsync(loop, "outer.1.inner", 3, statuses, key =>
        {
            lookups.Add(key);
            return Task.FromResult(false);
        });

        // Assert
        Assert.Equal(["outer.1.inner.1.start", "outer.1.inner.2.start"], admissions.Select(a => a.RuntimeStepKey));
        Assert.All(lookups, key => Assert.StartsWith("outer.1.inner.", key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RandomPrefixConsistentStates_AdmitTheSameKeysAsTheFullSetGate()
    {
        // Arrange
        var random = new Random(189);
        var mismatches = new List<string>();

        // Act
        for (var round = 0; round < 3000; round++)
        {
            var loop = Body(concurrencyLimit: 1 + random.Next(4), entryChildren: 1 + random.Next(2));
            var iterations = 1 + random.Next(40);
            var (statuses, ledger) = RandomState(random, loop, iterations);

            var expected = LoopAdmission.NextAdmissions(loop, LoopKey, iterations, statuses, ledger);
            var actual = await LoopAdmission.NextAdmissionsAsync(
                loop, LoopKey, iterations, statuses, key => Task.FromResult(ledger.Contains(key)));

            if (!expected.Select(a => a.RuntimeStepKey).SequenceEqual(actual.Select(a => a.RuntimeStepKey)))
            {
                mismatches.Add($"round {round}: [{string.Join(",", expected.Select(a => a.RuntimeStepKey))}] != [{string.Join(",", actual.Select(a => a.RuntimeStepKey))}]");
            }
        }

        // Assert
        Assert.Empty(mismatches);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A loop body with <paramref name="entryChildren"/> entry steps (<c>start</c>, <c>start2</c>)
    /// followed by a chained <c>done</c>.
    /// </summary>
    private static LoopStepMetadata Body(int concurrencyLimit, int entryChildren)
    {
        var steps = new StepCollection { ["start"] = new StepMetadata { Type = "Start" } };
        if (entryChildren > 1)
        {
            steps["start2"] = new StepMetadata { Type = "Start" };
        }

        steps["done"] = new StepMetadata
        {
            Type = "Done",
            RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Succeeded] }
        };

        return new LoopStepMetadata { Type = "ForEach", ConcurrencyLimit = concurrencyLimit, Steps = steps };
    }

    private static Dictionary<string, StepStatus> Settled(LoopStepMetadata loop, int settledIterations)
    {
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal) { [LoopKey] = StepStatus.Running };
        for (var i = 0; i < settledIterations; i++)
        {
            foreach (var child in loop.Steps.Keys)
            {
                statuses[$"{LoopKey}.{i}.{child}"] = StepStatus.Succeeded;
            }
        }

        return statuses;
    }

    /// <summary>
    /// Builds a state that honours the gate's started-prefix invariant: iterations below a random
    /// boundary are started — via a status row, a ledger row, or both, per entry child — and none at or
    /// above it is. Started iterations are a random mix of settled and in flight.
    /// </summary>
    private static (Dictionary<string, StepStatus> Statuses, HashSet<string> Ledger) RandomState(
        Random random,
        LoopStepMetadata loop,
        int iterations)
    {
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal) { [LoopKey] = StepStatus.Running };
        var ledger = new HashSet<string>(StringComparer.Ordinal);
        var boundary = random.Next(iterations + 1);
        var entries = LoopAdmission.EntrySteps(loop).Select(kvp => kvp.Key).ToArray();

        for (var i = 0; i < boundary; i++)
        {
            switch (random.Next(4))
            {
                case 0:
                    // Dispatched, not yet picked up: ledger rows only.
                    foreach (var entry in entries)
                    {
                        ledger.Add($"{LoopKey}.{i}.{entry}");
                    }

                    break;
                case 1:
                    // In flight: the first entry is running, its ledger row released for a re-poll.
                    statuses[$"{LoopKey}.{i}.{entries[0]}"] = StepStatus.Pending;
                    break;
                default:
                    // Settled: every child terminal, ledger rows kept.
                    foreach (var child in loop.Steps.Keys)
                    {
                        statuses[$"{LoopKey}.{i}.{child}"] = random.Next(5) == 0 ? StepStatus.Failed : StepStatus.Succeeded;
                        ledger.Add($"{LoopKey}.{i}.{child}");
                    }

                    break;
            }
        }

        return (statuses, ledger);
    }
}
