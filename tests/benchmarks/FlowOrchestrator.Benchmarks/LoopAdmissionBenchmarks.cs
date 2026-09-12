using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures <see cref="LoopAdmission.NextAdmissions"/>, the <c>ForEach</c> concurrency gate added
/// in 1.31.4 (issue #181). The engine calls it from
/// <c>FlowOrchestratorEngine.AdmitLoopIterationsAsync</c> on <b>every</b> step completion of a run
/// that has a parked loop, so its per-call cost is multiplied by
/// <c>iterations × childrenPerIteration</c> over the life of a loop run.
/// </summary>
/// <remarks>
/// <para>
/// The gate locates the first un-started iteration by walking forward from index 0
/// (<c>while (firstPending &lt; iterations &amp;&amp; IsIterationStarted(...)) firstPending++;</c>).
/// Under the default sequential <c>ConcurrencyLimit = 1</c> the loop body completes in index order,
/// so the walk re-probes every already-started iteration on every pass — index 0..k for the pass
/// that admits iteration k+1. Summed over a run that is O(iterations²) probes, and each probe
/// formats a <c>"{loopKey}.{index}."</c> prefix plus one concatenation per entry child.
/// </para>
/// <para>
/// <see cref="AdmitMidRun"/> is the shape that matters: the loop is half-way through, which is the
/// average cost of the whole run. <see cref="AdmitFirstIteration"/> and
/// <see cref="AdmitLastIteration"/> bracket it, and their ratio is the direct measure of whether
/// the forward walk is linear in the number of already-started iterations.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class LoopAdmissionBenchmarks
{
    /// <summary>Total iteration count of the benchmarked <c>ForEach</c>.</summary>
    [Params(10, 100, 500)]
    public int Iterations { get; set; }

    private const int ChildrenPerIteration = 3;
    private const string LoopKey = "loop";

    private LoopStepMetadata _loopMetadata = null!;
    private Dictionary<string, StepStatus> _firstPass = null!;
    private Dictionary<string, StepStatus> _midRun = null!;
    private Dictionary<string, StepStatus> _lastIteration = null!;
    private IReadOnlySet<string> _noDispatches = null!;

    /// <summary>Builds the loop metadata and the three status maps under test.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var body = new StepCollection();
        for (var c = 0; c < ChildrenPerIteration; c++)
        {
            // child_0 is the only entry step; the rest chain off it, matching a typical
            // sequential loop body (start -> wait -> done).
            body[$"child_{c}"] = c == 0
                ? new StepMetadata { Type = "noop" }
                : new StepMetadata
                {
                    Type = "noop",
                    RunAfter = new RunAfterCollection
                    {
                        [$"child_{c - 1}"] = RunAfterCondition.Create([StepStatus.Succeeded])
                    }
                };
        }

        _loopMetadata = new LoopStepMetadata { Type = "foreach", Steps = body, ConcurrencyLimit = 1 };

        _noDispatches = new HashSet<string>(StringComparer.Ordinal);

        // Nothing started yet — the forward walk exits on its first probe.
        _firstPass = BuildStatuses(settledIterations: 0);

        // Half the iterations settled — the average pass of a sequential run.
        _midRun = BuildStatuses(settledIterations: Iterations / 2);

        // Every iteration but the last settled — the most expensive pass of the run.
        _lastIteration = BuildStatuses(settledIterations: Iterations - 1);
    }

    /// <summary>
    /// Builds a status map where iterations <c>0..settledIterations-1</c> are fully terminal and
    /// no later iteration has been handed out — the exact state a sequential loop is in when the
    /// admission gate runs.
    /// </summary>
    /// <param name="settledIterations">How many leading iterations are fully settled.</param>
    private Dictionary<string, StepStatus> BuildStatuses(int settledIterations)
    {
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            [LoopKey] = StepStatus.Running
        };

        for (var i = 0; i < settledIterations; i++)
        {
            for (var c = 0; c < ChildrenPerIteration; c++)
            {
                statuses[$"{LoopKey}.{i}.child_{c}"] = StepStatus.Succeeded;
            }
        }

        return statuses;
    }

    /// <summary>Best case: no iteration started, so the forward walk exits immediately.</summary>
    [Benchmark(Description = "NextAdmissions (nothing started — first pass)")]
    public int AdmitFirstIteration() =>
        LoopAdmission.NextAdmissions(_loopMetadata, LoopKey, Iterations, _firstPass, _noDispatches).Count;

    /// <summary>Average case: half the iterations settled, so the forward walk re-probes N/2.</summary>
    [Benchmark(Description = "NextAdmissions (half settled — mid-run pass)")]
    public int AdmitMidRun() =>
        LoopAdmission.NextAdmissions(_loopMetadata, LoopKey, Iterations, _midRun, _noDispatches).Count;

    /// <summary>Worst case: only the last iteration is un-started, so the walk re-probes N-1.</summary>
    [Benchmark(Description = "NextAdmissions (all but last settled — final pass)")]
    public int AdmitLastIteration() =>
        LoopAdmission.NextAdmissions(_loopMetadata, LoopKey, Iterations, _lastIteration, _noDispatches).Count;
}
