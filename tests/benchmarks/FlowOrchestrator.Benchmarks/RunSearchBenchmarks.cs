using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Quantifies the cost difference between the tiered RUN-search modes added in
/// v1.27.0 on <see cref="InMemoryFlowRunStore.GetRunsPageAsync(System.Guid?, string?, int, int, string?, bool, System.DateTimeOffset?, System.DateTimeOffset?)"/>.
/// <para>
/// The <c>quick</c> path (<c>deepSearch: false</c>) matches the search term only
/// against the run identity columns (id, flow name, trigger key, status, job id)
/// and short-circuits — its work is O(runs).
/// </para>
/// <para>
/// The <c>deep</c> path (<c>deepSearch: true</c>) additionally scans the step rows
/// for every run that survives the identity filter. In the in-memory store that
/// inner scan is <c>_steps.Values.Any(s =&gt; s.RunId == run.Id &amp;&amp; ...)</c>
/// (see <c>InMemoryFlowRunStore.MatchesRunSearch</c>), which walks the whole global
/// step keyspace per candidate run — so the cost is O(runs × total_steps) and
/// grows quadratically as run history accumulates.
/// </para>
/// <para>
/// The search term is chosen to appear <b>only</b> inside step <c>OutputJson</c>,
/// never in any identity column, so the quick path matches zero rows (the cheap
/// path) while the deep path performs the full step scan and returns matches (the
/// representative path). Both calls request a single page (<c>take: 20</c>),
/// matching the dashboard's default page size.
/// </para>
/// </summary>
/// <remarks>
/// Uses the in-process emit toolchain (<see cref="RunSearchConfig"/>) rather than
/// the default out-of-process job. The repo keeps stale git worktrees under
/// <c>.claude/worktrees/</c> that each contain a copy of this benchmark project;
/// BenchmarkDotNet's default toolchain discovers the duplicate <c>.csproj</c> by
/// assembly name and refuses to build the boilerplate. Running in-process skips
/// the generated project entirely. The subject is a pure in-memory store, so
/// in-process execution does not perturb the measurement.
/// <para>
/// A reduced job (3 warmup / 3 iterations) is used because the deep path at
/// <c>TotalRuns=10_000</c> is O(runs × total_steps) ≈ 6×10⁸ comparisons per
/// invocation and takes tens of seconds per op — a full job would run for hours.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(RunSearchConfig))]
public class RunSearchBenchmarks
{
    /// <summary>
    /// In-process, reduced-iteration configuration for <see cref="RunSearchBenchmarks"/>.
    /// </summary>
    private sealed class RunSearchConfig : ManualConfig
    {
        /// <summary>Initialises the config with the in-process emit toolchain and a short job.</summary>
        public RunSearchConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(3)
                .WithIterationCount(3)
                .WithLaunchCount(1));
        }
    }

    private const int StepsPerRun = 6;
    private const int PageSize = 20;

    /// <summary>
    /// A token embedded in exactly one step's output JSON per run, and in no
    /// identity column. Matching it forces the deep path to do real work while
    /// the quick path provably returns nothing.
    /// </summary>
    private const string DeepOnlyNeedle = "needle-7f3a";

    /// <summary>Total runs seeded into the store before each measurement.</summary>
    [Params(1_000, 10_000)]
    public int TotalRuns { get; set; }

    /// <summary>
    /// Selects the search tier under test: <see langword="false"/> = quick
    /// (identity-only), <see langword="true"/> = deep (identity + step scan).
    /// </summary>
    [Params(false, true)]
    public bool DeepSearch { get; set; }

    private InMemoryFlowRunStore _store = null!;

    /// <summary>
    /// Seeds <see cref="TotalRuns"/> completed runs, each with
    /// <see cref="StepsPerRun"/> steps carrying a non-trivial JSON output blob.
    /// The needle is planted in one step per run so the deep scan has to walk to
    /// it; identity columns never contain the needle so the quick path is a true
    /// negative.
    /// </summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _store = new InMemoryFlowRunStore();

        for (var i = 0; i < TotalRuns; i++)
        {
            var runId = Guid.NewGuid();
            await _store.StartRunAsync(
                flowId: Guid.Empty,
                flowName: "BenchFlow",
                runId: runId,
                triggerKey: "manual",
                triggerData: null,
                jobId: null);

            for (var s = 0; s < StepsPerRun; s++)
            {
                var stepKey = $"step_{s}";
                await _store.RecordStepStartAsync(runId, stepKey, "noop", inputJson: null, jobId: null);
                await _store.TryRecordDispatchAsync(runId, stepKey);
                await _store.TryClaimStepAsync(runId, stepKey);

                // Only the last step of each run carries the needle, so the deep
                // scan must enumerate past the earlier steps before it can match.
                var carriesNeedle = s == StepsPerRun - 1;
                await _store.RecordStepCompleteAsync(
                    runId, stepKey,
                    status: "Succeeded",
                    outputJson: BuildOutputJson(i, s, carriesNeedle),
                    errorMessage: null);
            }
        }
    }

    /// <summary>
    /// Runs a single search page against the store using the tier selected by
    /// <see cref="DeepSearch"/>. The result tuple is returned so the JIT cannot
    /// elide the call.
    /// </summary>
    [Benchmark(Description = "GetRunsPageAsync(search, take:20)")]
    public async Task<int> Search()
    {
        var (_, total) = await _store.GetRunsPageAsync(
            flowId: null,
            status: null,
            skip: 0,
            take: PageSize,
            search: DeepOnlyNeedle,
            deepSearch: DeepSearch);
        return total;
    }

    /// <summary>
    /// Builds a realistic, non-trivial output payload for a step. The needle is
    /// embedded as a field value only when <paramref name="carriesNeedle"/> is set
    /// so it lives exclusively in step output, never in a run identity column.
    /// </summary>
    /// <param name="runOrdinal">Sequential index of the run being seeded.</param>
    /// <param name="stepOrdinal">Index of the step within the run.</param>
    /// <param name="carriesNeedle">When <see langword="true"/>, plants the deep-only search token.</param>
    /// <returns>A JSON object string of a few hundred bytes.</returns>
    private static string BuildOutputJson(int runOrdinal, int stepOrdinal, bool carriesNeedle)
    {
        var correlation = carriesNeedle ? DeepOnlyNeedle : "ok";
        // Hand-built JSON (no serializer dependency) shaped like a typical step
        // output: a status block, a few scalar fields, and a small nested object.
        return string.Create(CultureInfo.InvariantCulture, $$"""
            {"status":"completed","stepOrdinal":{{stepOrdinal}},"runOrdinal":{{runOrdinal}},"correlation":"{{correlation}}","httpStatus":200,"durationMs":{{42 + stepOrdinal}},"payload":{"itemsProcessed":{{100 + runOrdinal % 50}},"warnings":0,"region":"westus2","retryable":false},"timestamp":"2026-05-24T12:00:00Z"}
            """);
    }
}
