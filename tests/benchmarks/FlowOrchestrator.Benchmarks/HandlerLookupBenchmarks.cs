using System.Collections.Frozen;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Before/after for the step-handler lookup in <c>DefaultStepExecutor</c>, which runs once per step
/// the engine executes.
/// </summary>
/// <remarks>
/// It used to be <c>_handlerMetadata.FirstOrDefault(h =&gt; string.Equals(h.Type, metadata.Type,
/// OrdinalIgnoreCase))</c> — O(registered handlers) plus a closure and an enumerator allocation on
/// every execution. The registry is fixed for the executor's lifetime, so it is now indexed once into
/// a <see cref="FrozenDictionary{TKey, TValue}"/>.
/// <para>
/// The absolute numbers here are small; the point of recording them is that the cost scales with the
/// number of registered handlers while the replacement does not, so the gap widens in exactly the
/// deployments that register the most handlers.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class HandlerLookupBenchmarks
{
    /// <summary>How many step handlers are registered with the host.</summary>
    [Params(8, 32, 128)]
    public int HandlerCount { get; set; }

    private sealed record Handler(string Type);

    private Handler[] _handlers = null!;
    private FrozenDictionary<string, Handler> _frozen = null!;
    private string _worstCaseType = null!;

    /// <summary>Builds the registry and picks the key the linear scan finds last.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _handlers = Enumerable.Range(0, HandlerCount).Select(i => new Handler($"StepType{i}")).ToArray();

        var byType = new Dictionary<string, Handler>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in _handlers.Where(h => h.Type is not null))
        {
            byType.TryAdd(h.Type, h);
        }

        _frozen = byType.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        // Worst case for the scan, and the honest one to measure: a handler registered late is not a
        // pathological input, it is simply whichever one the flow happens to use.
        _worstCaseType = $"StepType{HandlerCount - 1}";
    }

    /// <summary>The original scan: a closure over every registered handler.</summary>
    [Benchmark(Baseline = true, Description = "BEFORE: FirstOrDefault with closure")]
    public object? LinearScan() =>
        _handlers.FirstOrDefault(h => string.Equals(h.Type, _worstCaseType, StringComparison.OrdinalIgnoreCase));

    /// <summary>The shipped lookup.</summary>
    [Benchmark(Description = "AFTER: FrozenDictionary.TryGetValue")]
    public object? FrozenLookup()
    {
        _frozen.TryGetValue(_worstCaseType, out var handler);
        return handler;
    }
}
