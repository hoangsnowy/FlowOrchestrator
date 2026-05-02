using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures the per-call cost of the engine's hot-path reads on
/// <see cref="InMemoryFlowRunStore"/> as a function of total run history.
/// The pre-F1 implementation scanned the global step keyspace
/// (O(total_steps_in_history)); F1 added per-run secondary indexes that
/// reduce the cost to O(steps_in_one_run). A flat curve as
/// <see cref="TotalRuns"/> grows is the proof of the asymptotic win.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class InMemoryFlowRunStoreBenchmarks
{
    private const int StepsPerRun = 5;

    [Params(10, 100, 1000, 10_000)]
    public int TotalRuns { get; set; }

    private InMemoryFlowRunStore _store = null!;
    private Guid _targetRunId;

    [GlobalSetup]
    public async Task Setup()
    {
        _store = new InMemoryFlowRunStore();

        // Seed N runs, each with M steps. The "target" run is the median one
        // — we measure operations on it after the global keyspace is fully
        // populated, which is when the pre-F1 scan would be slowest.
        var runIds = new Guid[TotalRuns];
        for (var i = 0; i < TotalRuns; i++)
        {
            var runId = Guid.NewGuid();
            runIds[i] = runId;
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
                await _store.RecordStepCompleteAsync(
                    runId, stepKey,
                    status: "Succeeded",
                    outputJson: null,
                    errorMessage: null);
            }
        }

        _targetRunId = runIds[TotalRuns / 2];
    }

    [Benchmark(Description = "GetStepStatusesAsync (1 run among N)")]
    public async Task<IReadOnlyDictionary<string, FlowOrchestrator.Core.Abstractions.StepStatus>> GetStepStatuses()
    {
        return await _store.GetStepStatusesAsync(_targetRunId);
    }

    [Benchmark(Description = "GetClaimedStepKeysAsync (1 run among N)")]
    public async Task<IReadOnlyCollection<string>> GetClaimedKeys()
    {
        return await _store.GetClaimedStepKeysAsync(_targetRunId);
    }

    [Benchmark(Description = "GetDispatchedStepKeysAsync (1 run among N)")]
    public async Task<IReadOnlySet<string>> GetDispatchedKeys()
    {
        return await _store.GetDispatchedStepKeysAsync(_targetRunId);
    }
}
