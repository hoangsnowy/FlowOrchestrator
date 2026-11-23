using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures <see cref="RunTerminationClassifier.ComputeTerminalStatus"/>, which the engine calls
/// once per run completion and <c>FlowRunRecoveryHostedService</c> once per recovered run.
/// </summary>
/// <remarks>
/// The original classifier decided leaf-ness per status by scanning every other status and
/// resolving each candidate's <c>RunAfter</c> keys into runtime space (one interpolated string per
/// comparison), which is O(n²) in the run's step count (#189). The run shape is
/// <c>entry -> loop(child_0 -> child_1 -> child_2) -> after</c> with every step terminal — the
/// state the classifier sees on the pass that closes a successful loop run.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class RunTerminationClassifierBenchmarks
{
    /// <summary>Iteration count the loop step fanned out to.</summary>
    [Params(100, 500)]
    public int Iterations { get; set; }

    private const int ChildrenPerIteration = 3;

    private BenchFlow _flow = null!;
    private Dictionary<string, StepStatus> _allSucceeded = null!;
    private Dictionary<string, StepStatus> _bodySkipped = null!;

    /// <summary>Builds the loop flow and the two terminal status maps under test.</summary>
    [GlobalSetup]
    public void Setup()
    {
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

        _allSucceeded = BuildStatuses(StepStatus.Succeeded, StepStatus.Succeeded);
        _bodySkipped = BuildStatuses(StepStatus.Skipped, StepStatus.Skipped);
    }

    private Dictionary<string, StepStatus> BuildStatuses(StepStatus childStatus, StepStatus afterStatus)
    {
        var statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["entry"] = StepStatus.Succeeded,
            ["loop"] = StepStatus.Succeeded,
            ["after"] = afterStatus
        };

        for (var i = 0; i < Iterations; i++)
        {
            for (var c = 0; c < ChildrenPerIteration; c++)
            {
                statuses[$"loop.{i}.child_{c}"] = childStatus;
            }
        }

        return statuses;
    }

    /// <summary>Classifies a loop run whose every step succeeded.</summary>
    [Benchmark(Description = "ComputeTerminalStatus (loop run, all succeeded)")]
    public string AllSucceeded() => RunTerminationClassifier.ComputeTerminalStatus(_flow, _allSucceeded);

    /// <summary>Classifies a loop run whose body and downstream step were all skipped.</summary>
    [Benchmark(Description = "ComputeTerminalStatus (loop run, body skipped)")]
    public string BodySkipped() => RunTerminationClassifier.ComputeTerminalStatus(_flow, _bodySkipped);

    private sealed class BenchFlow(FlowManifest manifest) : IFlowDefinition
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Version { get; set; } = "1.0.0";
        public FlowManifest Manifest { get; set; } = manifest;
    }
}
