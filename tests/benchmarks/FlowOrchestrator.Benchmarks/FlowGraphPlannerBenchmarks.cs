using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures <see cref="FlowGraphPlanner.Evaluate"/> on a linear flow without
/// loops or foreach — the common case where F2's manifest-key cache returns
/// the cached array directly without a SortedSet build, sort, or ToArray.
/// The benchmark sweeps the flow's step count to expose how the per-call cost
/// scales with manifest size.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class FlowGraphPlannerBenchmarks
{
    [Params(5, 25, 100)]
    public int StepCount { get; set; }

    private FlowGraphPlanner _planner = null!;
    private TestFlow _flow = null!;
    private Dictionary<string, StepStatus> _statuses = null!;

    [GlobalSetup]
    public void Setup()
    {
        _planner = new FlowGraphPlanner();

        var manifest = new FlowManifest();
        for (var i = 0; i < StepCount; i++)
        {
            var key = $"step_{i:D4}";
            var meta = new StepMetadata { Type = "noop" };
            if (i > 0)
            {
                meta.RunAfter[$"step_{i - 1:D4}"] = new RunAfterCondition
                {
                    Statuses = new[] { StepStatus.Succeeded }
                };
            }
            manifest.Steps[key] = meta;
        }
        _flow = new TestFlow(manifest);

        // Half the steps already succeeded, half still to evaluate. This
        // matches what Evaluate sees mid-run.
        _statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal);
        for (var i = 0; i < StepCount / 2; i++)
        {
            _statuses[$"step_{i:D4}"] = StepStatus.Succeeded;
        }
    }

    [Benchmark(Description = "Evaluate (linear flow, half complete)")]
    public FlowGraphEvaluation Evaluate()
    {
        return _planner.Evaluate(_flow, _statuses);
    }

    private sealed class TestFlow : IFlowDefinition
    {
        public TestFlow(FlowManifest manifest)
        {
            Manifest = manifest;
        }
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; } = "TestFlow";
        public string Version { get; set; } = "1.0.0";
        public FlowManifest Manifest { get; set; }
    }
}
