using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Benchmarks.Support;

/// <summary>What one ForEach run cost in store traffic.</summary>
/// <param name="Items">Iterations the loop ran.</param>
/// <param name="StepExecutions">Step executions the run took — the number of continuations it ran.</param>
/// <param name="StoreCalls">Calls the engine made on the counted run and runtime stores.</param>
/// <param name="StatusReads">Calls of any <c>GetStepStatusesAsync</c> overload.</param>
/// <param name="StatusRows">Rows those calls returned in total.</param>
/// <param name="KeySetRows">Rows the full-set claim and dispatch-ledger reads returned in total.</param>
public readonly record struct ForEachRunCounts(
    int Items,
    int StepExecutions,
    long StoreCalls,
    long StatusReads,
    long StatusRows,
    long KeySetRows)
{
    /// <summary>Status rows read per step completion: flat in <see cref="Items"/> is the pass condition.</summary>
    public double StatusRowsPerCompletion => (double)StatusRows / StepExecutions;

    /// <summary>Claim and ledger rows read per step completion.</summary>
    public double KeySetRowsPerCompletion => (double)KeySetRows / StepExecutions;

    /// <summary>Store calls per step completion.</summary>
    public double CallsPerCompletion => (double)StoreCalls / StepExecutions;
}

/// <summary>
/// Runs <c>prepare → ForEach(items){ c0 → c1 } → done</c> — the stress harness's <c>foreach-scaling</c>
/// shape — through the real <see cref="FlowOrchestratorEngine"/> on the in-memory store, with a FIFO
/// in-process dispatcher, and counts every store call the engine makes.
/// </summary>
/// <remarks>
/// The step executor is a stub: the measurement is the engine's own per-completion work (status reads,
/// planner evaluation, loop admission and barrier, completion gate), not handler cost. The loop step
/// returns what <see cref="ForEachStepHandler"/> returns: <c>Running</c>, its iteration count, and a
/// dispatch hint for the first <c>ConcurrencyLimit</c> iterations.
/// </remarks>
public sealed class ForEachRunDriver
{
    private readonly int _items;
    private readonly int _concurrencyLimit;
    private readonly Queue<IStepInstance> _queue = new();
    private readonly FlowDefinitionStub _flow;
    private readonly InMemoryFlowRunStore _store = new();
    private readonly StoreReadCounter _counter = new();
    private readonly FlowOrchestratorEngine _engine;

    /// <summary>Builds a driver for one run.</summary>
    /// <param name="items">Iterations the ForEach runs.</param>
    /// <param name="concurrencyLimit">The loop's <see cref="LoopStepMetadata.ConcurrencyLimit"/>.</param>
    public ForEachRunDriver(int items, int concurrencyLimit)
    {
        _items = items;
        _concurrencyLimit = concurrencyLimit;
        _flow = new FlowDefinitionStub(BuildManifest(items, concurrencyLimit));

        _engine = new FlowOrchestratorEngine(
            new QueueDispatcher(_queue),
            new FlowGraphPlanner(),
            new StubExecutor(this),
            new InMemoryFlowStore(),
            CountingStoreProxy<IFlowRunStore>.Wrap(_store, _counter),
            new InMemoryOutputsRepository(),
            new ExecutionContextAccessor(),
            new FlowRegistry([_flow]),
            [CountingStoreProxy<IFlowRunRuntimeStore>.Wrap(_store, _counter)],
            [_store],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            NullLogger<FlowOrchestratorEngine>.Instance,
            NoopFlowEventNotifier.Instance);
    }

    /// <summary>Triggers the run, drains every dispatched step in FIFO order, and returns the counts.</summary>
    /// <returns>The store traffic of the whole run.</returns>
    /// <exception cref="InvalidOperationException">The run did not finish <c>Succeeded</c>.</exception>
    public async Task<ForEachRunCounts> RunAsync()
    {
        var runId = Guid.NewGuid();
        await _engine.TriggerAsync(new TriggerContext
        {
            RunId = runId,
            Flow = _flow,
            Trigger = new Trigger("manual", "Manual", null)
        }).ConfigureAwait(false);

        var executions = 0;
        while (_queue.TryDequeue(out var step))
        {
            executions++;
            await _engine.RunStepAsync(new CoreExecutionContext { RunId = runId }, _flow, step).ConfigureAwait(false);
        }

        var status = (await _store.GetRunDetailAsync(runId).ConfigureAwait(false))?.Status;
        if (status != "Succeeded")
        {
            throw new InvalidOperationException($"Run finished '{status}' after {executions} step executions.");
        }

        return new ForEachRunCounts(
            _items, executions, _counter.Calls, _counter.StatusReads, _counter.StatusRows, _counter.KeySetRows);
    }

    private static FlowManifest BuildManifest(int items, int concurrencyLimit) => new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["prepare"] = new StepMetadata { Type = "Echo" },
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = Enumerable.Range(0, items).Cast<object?>().ToList(),
                ConcurrencyLimit = concurrencyLimit,
                RunAfter = new RunAfterCollection { ["prepare"] = [StepStatus.Succeeded] },
                Steps = new StepCollection
                {
                    ["c0"] = new StepMetadata { Type = "Echo" },
                    ["c1"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection { ["c0"] = [StepStatus.Succeeded] }
                    }
                }
            },
            ["done"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
            }
        }
    };

    private IStepResult ResultFor(IStepInstance step)
    {
        if (step.Key != "loop")
        {
            return new StepResult { Key = step.Key, Status = StepStatus.Succeeded, Result = new { ok = true } };
        }

        var window = Math.Min(_items, _concurrencyLimit);
        var children = new List<StepDispatchRequest>(window);
        for (var index = 0; index < window; index++)
        {
            children.Add(new StepDispatchRequest($"loop.{index}.c0", "Echo", new Dictionary<string, object?>()));
        }

        return new StepResult
        {
            Key = step.Key,
            Status = StepStatus.Running,
            Result = new { iterations = _items },
            DispatchHint = new StepDispatchHint(children)
        };
    }

    private sealed class StubExecutor(ForEachRunDriver owner) : IStepExecutor
    {
        public ValueTask<IStepResult> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
            => new(owner.ResultFor(step));
    }

    private sealed class QueueDispatcher(Queue<IStepInstance> queue) : IStepDispatcher
    {
        public ValueTask<string?> EnqueueStepAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step, CancellationToken ct = default)
        {
            queue.Enqueue(step);
            return new ValueTask<string?>("job-" + step.Key);
        }

        public ValueTask<string?> ScheduleStepAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step, TimeSpan delay, CancellationToken ct = default)
            => EnqueueStepAsync(context, flow, step, ct);
    }

    private sealed class FlowDefinitionStub(FlowManifest manifest) : IFlowDefinition
    {
        public Guid Id { get; } = Guid.NewGuid();

        public string Version => "1.0.0";

        public FlowManifest Manifest { get; set; } = manifest;
    }
}
