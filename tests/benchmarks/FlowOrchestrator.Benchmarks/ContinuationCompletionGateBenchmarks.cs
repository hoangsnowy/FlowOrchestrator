using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures the run-completion gate at the bottom of
/// <c>FlowOrchestratorEngine.RunGraphContinuationAsync</c>, which asks "does any claimed step lack
/// a status row?" before it is allowed to close the run.
/// </summary>
/// <remarks>
/// <para>
/// The shipped form is
/// <c>claimed.Except(statuses.Keys, StringComparer.Ordinal).Any()</c>.
/// <see cref="Enumerable.Except{TSource}(IEnumerable{TSource}, IEnumerable{TSource}, IEqualityComparer{TSource}?)"/>
/// builds a hash set from its <i>second</i> argument before yielding anything, so the cost is
/// proportional to the number of <b>status rows in the run</b> — the large side — even though
/// <c>claimed</c> holds only the handful of steps currently in flight.
/// </para>
/// <para>
/// The gate runs on every step completion that did not enqueue new work, so on a run with S status
/// rows the engine pays O(S) hashing and an S-entry set allocation S times over the run's life.
/// <see cref="ContainsKeyProbe"/> is the equivalent formulation that walks the small side instead;
/// the ratio between the two is the finding.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class ContinuationCompletionGateBenchmarks
{
    /// <summary>Number of status rows in the run — a ForEach run reaches the high end easily.</summary>
    [Params(25, 300, 1500)]
    public int StatusRows { get; set; }

    private Dictionary<string, StepStatus> _statuses = null!;
    private List<string> _claimed = null!;

    /// <summary>Builds a status map of the requested size plus a small in-flight claim list.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal);
        for (var i = 0; i < StatusRows; i++)
        {
            _statuses[$"loop.{i / 3}.child_{i % 3}"] = StepStatus.Succeeded;
        }

        // The realistic claimed set: the step that just completed, still holding its execution
        // claim, and which therefore does have a status row. The gate returns false.
        _claimed = ["loop.0.child_0", "loop.0.child_1"];
    }

    /// <summary>The shipped form — hashes the full status-key set on every call.</summary>
    [Benchmark(Baseline = true, Description = "claimed.Except(statuses.Keys).Any()")]
    public bool ExceptAny() =>
        _claimed.Except(_statuses.Keys, StringComparer.Ordinal).Any();

    /// <summary>The small-side equivalent — one dictionary probe per claimed key, no allocation.</summary>
    [Benchmark(Description = "foreach claimed: !statuses.ContainsKey(c)")]
    public bool ContainsKeyProbe()
    {
        foreach (var key in _claimed)
        {
            if (!_statuses.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }
}
