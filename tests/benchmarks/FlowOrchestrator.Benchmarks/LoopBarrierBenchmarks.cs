using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures the per-step-completion cost the ForEach completion barrier (issue #169) adds to
/// <c>FlowOrchestratorEngine.RunGraphContinuationAsync</c>.
/// </summary>
/// <remarks>
/// Two distinct populations are measured:
/// <list type="bullet">
/// <item><description>
/// The candidate-set build — <c>RunningLoopKeys</c> (every parked scoped step in the run) —
/// paid by <b>every</b> step completion of <b>every</b> flow, including flows that contain no
/// loop at all. This is the tax the feature levies on the common linear path.
/// </description></item>
/// <item><description>
/// <c>AllIterationsSettled</c> over an N-iteration loop body — paid only by loop runs, once per
/// child completion, so a 100-iteration loop pays it ~300 times over the run's life.
/// </description></item>
/// </list>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class LoopBarrierBenchmarks
{
    /// <summary>Iteration count of the benchmarked loop body.</summary>
    [Params(10, 100)]
    public int Iterations { get; set; }

    private const int ChildrenPerIteration = 3;

    private BenchFlow _linearFlow = null!;
    private BenchFlow _loopFlow = null!;
    private Dictionary<string, StepStatus> _allTerminal = null!;
    private Dictionary<string, StepStatus> _lastIterationOutstanding = null!;
    private Dictionary<string, StepStatus> _firstIterationOutstanding = null!;
    private Dictionary<string, StepStatus> _linearStatuses = null!;

    /// <summary>Builds a linear flow, a loop flow, and the three status maps under test.</summary>
    [GlobalSetup]
    public void Setup()
    {
        // Linear flow: 25 top-level steps, no scoped step anywhere.
        var linear = new FlowManifest();
        for (var i = 0; i < 25; i++)
        {
            linear.Steps[$"step_{i:D4}"] = new StepMetadata { Type = "noop" };
        }
        _linearFlow = new BenchFlow(linear);

        // Loop flow: entry -> loop(3 children) -> after.
        var loopBody = new StepCollection();
        for (var c = 0; c < ChildrenPerIteration; c++)
        {
            loopBody[$"child_{c}"] = new StepMetadata { Type = "noop" };
        }

        var loopManifest = new FlowManifest();
        loopManifest.Steps["entry"] = new StepMetadata { Type = "noop" };
        loopManifest.Steps["loop"] = new LoopStepMetadata { Type = "foreach", Steps = loopBody };
        loopManifest.Steps["after"] = new StepMetadata { Type = "noop" };
        _loopFlow = new BenchFlow(loopManifest);

        // Linear run mid-flight: everything before step 12 done, step 12 Running.
        _linearStatuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal);
        for (var i = 0; i < 13; i++)
        {
            _linearStatuses[$"step_{i:D4}"] = i == 12 ? StepStatus.Running : StepStatus.Succeeded;
        }

        _allTerminal = BuildStatuses(Iterations, outstandingIteration: -1);
        _lastIterationOutstanding = BuildStatuses(Iterations, outstandingIteration: Iterations - 1);
        _firstIterationOutstanding = BuildStatuses(Iterations, outstandingIteration: 0);
    }

    private Dictionary<string, StepStatus> BuildStatuses(int iterations, int outstandingIteration)
    {
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["entry"] = StepStatus.Succeeded,
            ["loop"] = StepStatus.Running
        };

        for (var i = 0; i < iterations; i++)
        {
            for (var c = 0; c < ChildrenPerIteration; c++)
            {
                if (i == outstandingIteration && c == ChildrenPerIteration - 1)
                {
                    statuses[$"loop.{i}.child_{c}"] = StepStatus.Running;
                    continue;
                }

                statuses[$"loop.{i}.child_{c}"] = StepStatus.Succeeded;
            }
        }

        return statuses;
    }

    /// <summary>
    /// Whole-run parked-loop scan on a linear flow — the shape the engine settles with after the
    /// candidate set was widened from "loops enclosing the completed step" to "every Running
    /// scoped step in the run". Pays one dictionary enumeration per step completion even when the
    /// flow contains no scoped step at all.
    /// </summary>
    [Benchmark(Description = "RunningLoopKeys (linear run, 13 status rows)")]
    public int RunningLoopKeys_Linear() =>
        LoopBarrier.RunningLoopKeys(_linearFlow, _linearStatuses).Count;

    /// <summary>Whole-run parked-loop scan over an N-iteration loop run's status map.</summary>
    [Benchmark(Description = "RunningLoopKeys (loop run, all iterations terminal)")]
    public int RunningLoopKeys_LoopRun() =>
        LoopBarrier.RunningLoopKeys(_loopFlow, _allTerminal).Count;

    /// <summary>Full scan: every iteration terminal, so the barrier walks all N iterations.</summary>
    [Benchmark(Description = "AllIterationsSettled (all terminal — full scan)")]
    public bool AllSettled_FullScan() =>
        LoopBarrier.AllIterationsSettled(_loopFlow, "loop", Iterations, _allTerminal);

    /// <summary>Worst realistic case: only the last iteration is outstanding, so N-1 are scanned.</summary>
    [Benchmark(Description = "AllIterationsSettled (last iteration outstanding)")]
    public bool AllSettled_LastOutstanding() =>
        LoopBarrier.AllIterationsSettled(_loopFlow, "loop", Iterations, _lastIterationOutstanding);

    /// <summary>Best case: iteration 0 is outstanding, so the scan exits on the first probe.</summary>
    [Benchmark(Description = "AllIterationsSettled (first iteration outstanding — early exit)")]
    public bool AllSettled_EarlyExit() =>
        LoopBarrier.AllIterationsSettled(_loopFlow, "loop", Iterations, _firstIterationOutstanding);

    private sealed class BenchFlow(FlowManifest manifest) : IFlowDefinition
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Version { get; set; } = "1.0.0";
        public FlowManifest Manifest { get; set; } = manifest;
    }
}
