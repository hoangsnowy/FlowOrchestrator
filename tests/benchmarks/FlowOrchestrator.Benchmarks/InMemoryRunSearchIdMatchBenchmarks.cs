using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures the run-identity text match of <see cref="InMemoryFlowRunStore"/>'s two search entry
/// points, which before #189 formatted <c>run.Id.ToString()</c> — a 36-char string, ~96 bytes — for
/// every candidate run of every search.
/// </summary>
/// <remarks>
/// The search term matches no run, so every run is a candidate that goes through the full identity
/// check; step payloads are excluded so the measurement is the identity match alone.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class InMemoryRunSearchIdMatchBenchmarks
{
    /// <summary>Total runs seeded into the store.</summary>
    [Params(1_000, 10_000)]
    public int TotalRuns { get; set; }

    private const string Needle = "no-such-term";

    private InMemoryFlowRunStore _store = null!;
    private RunSearchQuery _query = null!;

    /// <summary>Seeds <see cref="TotalRuns"/> header-only runs.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _store = new InMemoryFlowRunStore();
        for (var i = 0; i < TotalRuns; i++)
        {
            await _store.StartRunAsync(Guid.Empty, "BenchFlow", Guid.NewGuid(), "manual", null, null);
        }

        _query = new RunSearchQuery { Text = Needle, Take = 20 };
    }

    /// <summary>The legacy paged search, quick tier (identity columns only).</summary>
    [Benchmark(Description = "GetRunsPageAsync(search, deepSearch:false)")]
    public async Task<int> LegacyQuickSearch()
    {
        var (_, total) = await _store.GetRunsPageAsync(null, null, 0, 20, Needle, deepSearch: false);
        return total;
    }

    /// <summary>The structured search without step payloads.</summary>
    [Benchmark(Description = "SearchRunsAsync(Text, no payloads)")]
    public async Task<int> StructuredSearch() => (await _store.SearchRunsAsync(_query)).Runs.Count;
}
