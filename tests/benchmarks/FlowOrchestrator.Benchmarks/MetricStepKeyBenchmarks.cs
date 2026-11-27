using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Observability;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures <c>MetricStepKey.For</c>, called for the <c>step_key</c> tag of every step metric (duration,
/// queue delay, claim lost, poll attempts, skips) — i.e. several times per step completion.
/// </summary>
/// <remarks>
/// The runtime key used to be the tag directly (zero cost, unbounded cardinality). The template key must
/// stay allocation-free in steady state: a plain key is returned as is, and a loop key's template is
/// composed on the stack and resolved through a span alternate lookup.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class MetricStepKeyBenchmarks
{
    /// <summary>Key shape: top-level, one loop level, two nested loop levels.</summary>
    [Params("finalize", "process.37.validate", "process.37.inner.4.leaf")]
    public string Key { get; set; } = null!;

    /// <summary>Warms the template cache, as the first iteration of each loop does in production.</summary>
    [GlobalSetup]
    public void Setup() => MetricStepKey.For(Key);

    /// <summary>The shipped template-key mapping.</summary>
    [Benchmark(Description = "template key (shipped)")]
    public string Template() => MetricStepKey.For(Key);
}
