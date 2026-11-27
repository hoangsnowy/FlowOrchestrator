using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using FlowOrchestrator.Benchmarks.Support;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Runs a whole <c>prepare → ForEach(items){ c0 → c1 } → done</c> run through the real engine on the
/// in-memory store (issue #189 P0-1): the per-completion cost of the DAG continuation, summed over a run.
/// </summary>
/// <remarks>
/// <para>
/// The engine runs its continuation on every step completion. When that continuation reads every status
/// row of the run, a run of n iterations costs O(n²) — the stress harness measured
/// <c>status rows / iteration</c> rising from 413 at 100 items to 4013 at 1000 on SQL Server. Divide the
/// mean by <see cref="Items"/>: flat per-iteration time and allocation is the pass condition.
/// </para>
/// <para>
/// The store-row counts behind the same runs are printed by
/// <c>dotnet run -c Release --project tests/benchmarks/FlowOrchestrator.Benchmarks -- --store-reads</c>;
/// see <see cref="StoreReadReport"/>. They are deterministic, so they need no statistics.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(InProcessHostRuntimeConfig))]
public class ForEachContinuationBenchmarks
{
    /// <summary>Iterations of the ForEach — the n the per-iteration cost must not grow with.</summary>
    [Params(100, 1000)]
    public int Items { get; set; }

    /// <summary>The loop's concurrency limit: 1 is the sequential default, 16 the harness's parallel case.</summary>
    [Params(1, 16)]
    public int ConcurrencyLimit { get; set; }

    /// <summary>One complete run, from trigger to <c>Succeeded</c>.</summary>
    /// <returns>The run's store-traffic counts, returned so the work cannot be elided.</returns>
    [Benchmark]
    public Task<ForEachRunCounts> RunToCompletion() => new ForEachRunDriver(Items, ConcurrencyLimit).RunAsync();
}

/// <summary>
/// Prints the store-row counts of the runs <see cref="ForEachContinuationBenchmarks"/> times, as the
/// markdown table recorded in <c>docs/benchmarks/</c>.
/// </summary>
public static class StoreReadReport
{
    /// <summary>Runs each size and concurrency once and writes one table row per run.</summary>
    /// <param name="output">Where the table is written.</param>
    /// <returns>A task that completes when every run has finished.</returns>
    public static async Task WriteAsync(TextWriter output)
    {
        await output.WriteLineAsync("| Items | Limit | Step executions | Store calls / completion | Status reads | Status rows / completion | Claim + ledger rows / completion | Settled-count ms / run |").ConfigureAwait(false);
        await output.WriteLineAsync("|---:|---:|---:|---:|---:|---:|---:|---:|").ConfigureAwait(false);
        foreach (var limit in new[] { 1, 16 })
        {
            foreach (var items in new[] { 100, 250, 500, 1000 })
            {
                var counts = await new ForEachRunDriver(items, limit).RunAsync().ConfigureAwait(false);
                await output.WriteLineAsync(
                    $"| {items} | {limit} | {counts.StepExecutions} | {counts.CallsPerCompletion:F1} | {counts.StatusReads} | " +
                    $"{counts.StatusRowsPerCompletion:F1} | {counts.KeySetRowsPerCompletion:F1} | {counts.SettledCountMilliseconds:F1} |").ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// One pinned job, run in-process on the host runtime.
/// </summary>
/// <remarks>
/// This project targets net11.0. BenchmarkDotNet 0.15.8 has no <c>Net11_0</c> moniker: a
/// <c>RuntimeMoniker.Net10_0</c> job fails to restore (NU1201), and the default out-of-process toolchain
/// throws <see cref="NotImplementedException"/> validating the unrecognised net11 runtime. The in-process
/// toolchain needs neither.
/// </remarks>
public sealed class InProcessHostRuntimeConfig : ManualConfig
{
    /// <summary>Adds the single in-process job: 1 launch, 2 warm-up and 8 measured iterations.</summary>
    public InProcessHostRuntimeConfig()
    {
        AddJob(Job.Default
            .WithLaunchCount(1)
            .WithWarmupCount(2)
            .WithIterationCount(8)
            .WithToolchain(InProcessEmitToolchain.Instance));
    }
}
