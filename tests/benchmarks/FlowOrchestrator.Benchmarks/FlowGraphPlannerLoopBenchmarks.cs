using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures <see cref="FlowGraphPlanner.Evaluate"/> on a <b>loop</b> run, where the runtime status
/// map carries expanded iteration keys (<c>"loop.0.child_0"</c>) that are absent from the manifest.
/// </summary>
/// <remarks>
/// This is the slow path of <c>BuildKnownStepKeys</c>: the manifest-key cache cannot be returned
/// directly, so every call rebuilds a <see cref="SortedSet{T}"/> over
/// <c>manifest_keys + iterations x children</c>, re-splits every runtime key to find its scope
/// prefixes, and re-materialises the whole set with <c>ToArray</c>. The engine calls
/// <c>Evaluate</c> two or more times per step completion, and a loop run has
/// <c>iterations x children</c> completions, so the cost is quadratic in the iteration count over
/// the life of the run.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class FlowGraphPlannerLoopBenchmarks
{
    /// <summary>Iteration count the loop step fanned out to.</summary>
    [Params(10, 100)]
    public int Iterations { get; set; }

    private const int ChildrenPerIteration = 3;

    private FlowGraphPlanner _planner = null!;
    private BenchFlow _flow = null!;
    private Dictionary<string, StepStatus> _statuses = null!;

    /// <summary>Builds an entry -> loop(3 children) -> after flow with a half-complete loop body.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _planner = new FlowGraphPlanner();

        var loopBody = new StepCollection();
        for (var c = 0; c < ChildrenPerIteration; c++)
        {
            var child = new StepMetadata { Type = "noop" };
            if (c > 0)
            {
                child.RunAfter[$"child_{c - 1}"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded] };
            }
            loopBody[$"child_{c}"] = child;
        }

        var manifest = new FlowManifest();
        manifest.Steps["entry"] = new StepMetadata { Type = "noop" };
        manifest.Steps["loop"] = new LoopStepMetadata { Type = "foreach", Steps = loopBody };
        manifest.Steps["loop"].RunAfter["entry"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded] };
        var after = new StepMetadata { Type = "noop" };
        after.RunAfter["loop"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded] };
        manifest.Steps["after"] = after;
        _flow = new BenchFlow(manifest);

        // Mid-run: the loop is parked on its barrier and half the iterations are done.
        _statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["entry"] = StepStatus.Succeeded,
            ["loop"] = StepStatus.Running
        };

        for (var i = 0; i < Iterations / 2; i++)
        {
            for (var c = 0; c < ChildrenPerIteration; c++)
            {
                _statuses[$"loop.{i}.child_{c}"] = StepStatus.Succeeded;
            }
        }
    }

    /// <summary>One <c>Evaluate</c> call over the expanded loop key space.</summary>
    [Benchmark(Description = "Evaluate (loop run, expanded iteration keys)")]
    public FlowGraphEvaluation Evaluate() => _planner.Evaluate(_flow, _statuses);

    private sealed class BenchFlow(FlowManifest manifest) : IFlowDefinition
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Version { get; set; } = "1.0.0";
        public FlowManifest Manifest { get; set; } = manifest;
    }
}
