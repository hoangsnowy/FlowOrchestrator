using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Testing.Shared;

/// <summary>
/// The real <see cref="FlowOrchestratorEngine"/> over a real store (in-memory, SQL Server or PostgreSQL),
/// with an in-process queue as its runtime: dispatched steps wait in a list until a test runs them, one at a
/// time, in a chosen pair, or by a pool of concurrent workers.
/// </summary>
/// <remarks>
/// Linked into the unit and the storage integration projects, so the frontier scenarios (#189 P0-1) run
/// unchanged against every built-in store. The step executor is supplied by the test; every dispatch is
/// counted per step key, which is how "dispatched exactly once, never zero" is asserted.
/// </remarks>
internal sealed class StoreEngineRig
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(60);

    private readonly IFlowDefinition _flow;
    private readonly IFlowRunStore _runStore;
    private readonly List<IStepInstance> _pending = [];
    private readonly Dictionary<string, int> _dispatches = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changed = new(0);
    private int _busy;

    /// <summary>Builds an engine over the given stores.</summary>
    /// <param name="flow">The flow the rig runs.</param>
    /// <param name="runStore">Run store.</param>
    /// <param name="runtimeStore">Runtime store; usually the same object as <paramref name="runStore"/>.</param>
    /// <param name="controlStore">Run-control store; usually the same object as <paramref name="runStore"/>.</param>
    /// <param name="outputs">Outputs repository.</param>
    /// <param name="execute">Produces each step's result; may block or await to shape an interleaving.</param>
    public StoreEngineRig(
        IFlowDefinition flow,
        IFlowRunStore runStore,
        IFlowRunRuntimeStore runtimeStore,
        IFlowRunControlStore controlStore,
        IOutputsRepository outputs,
        Func<IStepInstance, ValueTask<IStepResult>> execute)
    {
        _flow = flow;
        _runStore = runStore;

        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.EnqueueStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(call => Enqueue(call.Arg<IStepInstance>()));
        dispatcher.ScheduleStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => Enqueue(call.Arg<IStepInstance>()));

        var executor = Substitute.For<IStepExecutor>();
        executor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(call => execute(call.Arg<IStepInstance>()));

        // A lease keeper, as AddFlowOrchestrator() registers one: every step then runs under an execution
        // lease, the production shape the frontier scenarios must hold under.
        var controlOptions = new FlowRunControlOptions();
        Engine = new FlowOrchestratorEngine(
            dispatcher,
            new FlowGraphPlanner(),
            executor,
            Substitute.For<IFlowStore>(),
            runStore,
            outputs,
            new ExecutionContextAccessor(),
            new FlowRegistry([flow]),
            [runtimeStore],
            [controlStore],
            controlOptions,
            new FlowObservabilityOptions { EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            NullLogger<FlowOrchestratorEngine>.Instance,
            NoopFlowEventNotifier.Instance,
            signalStore: null,
            leaseKeeper: new StepLeaseKeeper(controlOptions));
    }

    /// <summary>The engine under test.</summary>
    public FlowOrchestratorEngine Engine { get; }

    /// <summary>How many times the runtime was handed <paramref name="stepKey"/>.</summary>
    public int DispatchCount(string stepKey)
    {
        lock (_pending)
        {
            return _dispatches.GetValueOrDefault(stepKey);
        }
    }

    /// <summary>Every dispatched step key with its dispatch count.</summary>
    public IReadOnlyDictionary<string, int> Dispatches
    {
        get
        {
            lock (_pending)
            {
                return new Dictionary<string, int>(_dispatches, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Triggers the flow manually and returns the run id.</summary>
    /// <param name="body">Trigger payload.</param>
    public async Task<Guid> TriggerAsync(object? body = null)
    {
        var context = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = _flow,
            Trigger = new Trigger("manual", "Manual", body)
        };

        await Engine.TriggerAsync(context);
        return context.RunId;
    }

    /// <summary>Runs the queued steps with the given keys at the same time and waits for both.</summary>
    /// <param name="first">Key of one queued step.</param>
    /// <param name="second">Key of another queued step.</param>
    public Task RunConcurrentlyAsync(string first, string second)
    {
        var a = Take(first);
        var b = Take(second);
        return Task.WhenAll(Task.Run(() => RunAsync(a)), Task.Run(() => RunAsync(b)));
    }

    /// <summary>
    /// Runs queued steps on <paramref name="workers"/> concurrent workers until nothing is queued and nothing
    /// is running.
    /// </summary>
    /// <param name="workers">Number of concurrent workers.</param>
    public async Task DrainAsync(int workers = 1)
    {
        using var timeout = new CancellationTokenSource(DrainTimeout);
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() => WorkAsync(timeout.Token))));
    }

    /// <summary>The run's persisted status.</summary>
    public async Task<string?> RunStatusAsync(Guid runId) => (await _runStore.GetRunDetailAsync(runId))?.Status;

    /// <summary>The persisted status of one step, or <see langword="null"/> when it has no row.</summary>
    public async Task<string?> StepStatusAsync(Guid runId, string stepKey)
        => (await _runStore.GetRunDetailAsync(runId))?.Steps?.FirstOrDefault(s => s.StepKey == stepKey)?.Status;

    private async Task WorkAsync(CancellationToken timeout)
    {
        while (true)
        {
            IStepInstance? step = null;
            lock (_pending)
            {
                if (_pending.Count > 0)
                {
                    step = _pending[0];
                    _pending.RemoveAt(0);
                    _busy++;
                }
                else if (_busy == 0)
                {
                    // Nothing queued and nothing running: no step can enqueue more work. Wake the others.
                    _changed.Release(64);
                    return;
                }
            }

            if (step is null)
            {
                await _changed.WaitAsync(timeout);
                continue;
            }

            try
            {
                await RunAsync(step);
            }
            finally
            {
                lock (_pending)
                {
                    _busy--;
                }

                _changed.Release();
            }
        }
    }

    private Task RunAsync(IStepInstance step)
        => Engine.RunStepAsync(new CoreExecutionContext { RunId = step.RunId }, _flow, step).AsTask();

    private IStepInstance Take(string stepKey)
    {
        lock (_pending)
        {
            var index = _pending.FindIndex(s => string.Equals(s.Key, stepKey, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Step '{stepKey}' is not queued. Queued: {string.Join(", ", _pending.Select(s => s.Key))}.");
            }

            var step = _pending[index];
            _pending.RemoveAt(index);
            return step;
        }
    }

    private ValueTask<string?> Enqueue(IStepInstance step)
    {
        lock (_pending)
        {
            _pending.Add(step);
            _dispatches[step.Key] = _dispatches.GetValueOrDefault(step.Key) + 1;
        }

        _changed.Release();
        return new ValueTask<string?>("job-" + step.Key);
    }
}

/// <summary>Flows and step results shared by the frontier scenarios.</summary>
internal static class FrontierFlows
{
    /// <summary>Builds a flow definition substitute over <paramref name="steps"/>.</summary>
    public static IFlowDefinition Flow(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Version.Returns("1.0.0");
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = steps
        });
        return flow;
    }

    /// <summary><c>a ∥ b → join → end</c>.</summary>
    public static IFlowDefinition Join() => Flow(new StepCollection
    {
        ["a"] = new StepMetadata { Type = "Echo" },
        ["b"] = new StepMetadata { Type = "Echo" },
        ["join"] = new StepMetadata
        {
            Type = "Echo",
            RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded], ["b"] = [StepStatus.Succeeded] }
        },
        ["end"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["join"] = [StepStatus.Succeeded] } }
    });

    /// <summary><c>loop(items, limit){ c0 → c1 } → done</c>.</summary>
    public static IFlowDefinition Loop(int items, int limit) => Flow(new StepCollection
    {
        ["loop"] = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = Enumerable.Range(0, items).Cast<object?>().ToList(),
            ConcurrencyLimit = limit,
            Steps = new StepCollection
            {
                ["c0"] = new StepMetadata { Type = "Echo" },
                ["c1"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["c0"] = [StepStatus.Succeeded] } }
            }
        },
        ["done"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] } }
    });

    /// <summary>
    /// What <see cref="ForEachStepHandler"/> returns for a loop: <c>Running</c>, the iteration count, and a
    /// dispatch hint for the entry children of the first <paramref name="limit"/> iterations.
    /// </summary>
    public static IStepResult ParkedLoop(string loopKey, int items, int limit, params string[] entryChildren)
    {
        var children = new List<StepDispatchRequest>();
        for (var index = 0; index < Math.Min(items, limit); index++)
        {
            foreach (var child in entryChildren)
            {
                children.Add(new StepDispatchRequest($"{loopKey}.{index}.{child}", "Echo", new Dictionary<string, object?>()));
            }
        }

        return new StepResult
        {
            Key = loopKey,
            Status = StepStatus.Running,
            Result = new { iterations = items },
            DispatchHint = new StepDispatchHint(children)
        };
    }

    /// <summary>A plain successful result.</summary>
    public static IStepResult Succeeded(string stepKey)
        => new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };
}
