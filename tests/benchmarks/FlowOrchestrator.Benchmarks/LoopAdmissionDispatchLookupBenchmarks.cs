using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures how the engine's loop-admission pass reads the dispatch ledger. Before #189 it fetched
/// the run's whole ledger (<c>GetDispatchedStepKeysAsync</c>) on every pass while a loop was parked,
/// a set that grows by one row per dispatched step; the admission gate only ever probes a handful of
/// keys around the started-iteration boundary.
/// </summary>
/// <remarks>
/// Shape: a sequential (<c>ConcurrencyLimit = 1</c>) <c>ForEach</c> with three chained children, half
/// of its iterations settled, every executed step holding a ledger row — the average admission pass
/// of the run. Backed by <see cref="InMemoryFlowRunStore"/>, so it measures the in-process cost only;
/// on SQL Server / PostgreSQL the full-set read additionally ships one row per dispatched step over
/// the wire.
/// <para>
/// <see cref="LedgerFullSet"/> and <see cref="LedgerPointLookups"/> isolate the ledger read itself;
/// <see cref="AdmissionWithFullSet"/> and <see cref="AdmissionWithPointLookups"/> are the whole
/// admission decision (ledger read + gate), which also carries the gate's backwards active-count
/// walk.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class LoopAdmissionDispatchLookupBenchmarks
{
    /// <summary>Total iteration count of the benchmarked <c>ForEach</c>.</summary>
    [Params(100, 500)]
    public int Iterations { get; set; }

    private const int ChildrenPerIteration = 3;
    private const string LoopKey = "loop";

    private InMemoryFlowRunStore _store = null!;
    private Guid _runId;
    private LoopStepMetadata _loopMetadata = null!;
    private Dictionary<string, StepStatus> _statuses = null!;
    private Func<string, Task<bool>> _isDispatched = null!;
    private string _boundaryKey = null!;

    /// <summary>Seeds the store's ledger and builds the status map for a half-settled loop.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        var body = new StepCollection();
        for (var c = 0; c < ChildrenPerIteration; c++)
        {
            body[$"child_{c}"] = c == 0
                ? new StepMetadata { Type = "noop" }
                : new StepMetadata
                {
                    Type = "noop",
                    RunAfter = new RunAfterCollection
                    {
                        [$"child_{c - 1}"] = RunAfterCondition.Create([StepStatus.Succeeded])
                    }
                };
        }

        _loopMetadata = new LoopStepMetadata { Type = "foreach", Steps = body, ConcurrencyLimit = 1 };
        _store = new InMemoryFlowRunStore();
        _runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.Empty, "BenchFlow", _runId, "manual", null, null);
        await _store.TryRecordDispatchAsync(_runId, LoopKey);

        _statuses = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            [LoopKey] = StepStatus.Running
        };

        for (var i = 0; i < Iterations / 2; i++)
        {
            for (var c = 0; c < ChildrenPerIteration; c++)
            {
                var key = $"{LoopKey}.{i}.child_{c}";
                _statuses[key] = StepStatus.Succeeded;
                await _store.TryRecordDispatchAsync(_runId, key);
            }
        }

        var store = _store;
        var runId = _runId;
        _isDispatched = key => store.IsStepDispatchedAsync(runId, key);
        _boundaryKey = $"{LoopKey}.{Iterations / 2}.child_0";
    }

    /// <summary>The pre-#189 ledger read: materialise every dispatched key in the run.</summary>
    [Benchmark(Description = "Ledger read: GetDispatchedStepKeysAsync (full set)")]
    public async Task<int> LedgerFullSet() => (await _store.GetDispatchedStepKeysAsync(_runId)).Count;

    /// <summary>The post-#189 ledger read: the point lookups the gate actually needs.</summary>
    [Benchmark(Description = "Ledger read: IsStepDispatchedAsync (boundary probe)")]
    public async Task<bool> LedgerPointLookups()
        => await _store.IsStepDispatchedAsync(_runId, _boundaryKey);

    /// <summary>Whole admission decision with the full-set ledger read.</summary>
    [Benchmark(Description = "Admission: full-set ledger + NextAdmissions")]
    public async Task<int> AdmissionWithFullSet()
    {
        var dispatched = await _store.GetDispatchedStepKeysAsync(_runId);
        return LoopAdmission.NextAdmissions(_loopMetadata, LoopKey, Iterations, _statuses, dispatched).Count;
    }

    /// <summary>Whole admission decision with point lookups against the ledger.</summary>
    [Benchmark(Description = "Admission: point lookups + NextAdmissionsAsync")]
    public async Task<int> AdmissionWithPointLookups()
        => (await LoopAdmission.NextAdmissionsAsync(_loopMetadata, LoopKey, Iterations, _statuses, _isDispatched)).Count;
}
