using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Deterministic model of a deploy that changes a flow's manifest while runs are in flight: several
/// "replicas" — each an engine holding its own manifest version — share one run store, one flow store and
/// one durable step queue, the way Hangfire replicas share their storage.
/// </summary>
/// <remarks>
/// <para>
/// The step executor is the real <see cref="DefaultStepExecutor"/>, so a replica handed a step its manifest
/// does not contain goes through the production path. <c>Work</c> steps succeed; the <c>Gate</c> step stands in
/// for a <c>WaitForSignal</c>: it returns <see cref="StepStatus.Pending"/> (and is re-queued by the engine) until
/// <see cref="Signal"/> opens it.
/// </para>
/// <para>
/// A "start" of a replica runs the real <c>FlowSyncHostedService</c> (which writes that replica's manifest to the
/// flow store) and, unless suppressed, the real <see cref="FlowRunRecoveryHostedService"/> — the two hosted
/// services every host runs at startup. Nothing here sleeps or polls; the test picks which replica executes
/// each queued step.
/// </para>
/// </remarks>
internal sealed class ManifestDeployHarness
{
    /// <summary>Handler type of the steps that do the work and always succeed.</summary>
    public const string WorkType = "Work";

    /// <summary>Handler type of the signal-gated step.</summary>
    public const string GateType = "Gate";

    private readonly List<IStepInstance> _queue = [];
    private readonly List<(int Replica, string StepKey)> _executions = [];
    private readonly Dictionary<int, FlowOrchestratorEngine> _engines = [];
    private readonly Dictionary<int, IFlowDefinition> _manifests = [];
    private readonly IStepDispatcher _dispatcher;
    private readonly InMemoryOutputsRepository _outputs = new();
    private readonly IFlowStore _engineFlowStore;
    private volatile bool _signalled;

    /// <summary>Creates the shared infrastructure.</summary>
    /// <param name="losesQueuedStepsOnRestart">
    /// <see langword="true"/> models the in-process runtime: a restart drops every queued step (the queue is
    /// cleared) and recovery redelivers the ledgered ones. <see langword="false"/> models a durable runtime
    /// (Hangfire, Service Bus): queued steps survive a restart.
    /// </param>
    /// <param name="engineFlowStore">
    /// Optional decorator over <see cref="FlowStore"/> handed to every engine (not to the sync service), so a test
    /// can fail the engine's read of the registered manifest.
    /// </param>
    public ManifestDeployHarness(bool losesQueuedStepsOnRestart = false, Func<IFlowStore, IFlowStore>? engineFlowStore = null)
    {
        LosesQueuedStepsOnRestart = losesQueuedStepsOnRestart;
        _engineFlowStore = engineFlowStore?.Invoke(FlowStore) ?? FlowStore;
        _dispatcher = Substitute.For<IStepDispatcher>();
        _dispatcher.LosesQueuedStepsOnRestart.Returns(losesQueuedStepsOnRestart);
        _dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(call => Capture(call.Arg<IStepInstance>()));
        _dispatcher.ScheduleStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => Capture(call.Arg<IStepInstance>()));
    }

    /// <summary>Whether a restart drops the queue (in-process runtime).</summary>
    public bool LosesQueuedStepsOnRestart { get; }

    /// <summary>Run, runtime and control store shared by every replica.</summary>
    public InMemoryFlowRunStore Store { get; } = new();

    /// <summary>Flow-definition store shared by every replica; holds the manifest the last-started replica synced.</summary>
    public InMemoryFlowStore FlowStore { get; } = new();

    /// <summary>Keys currently queued, in queue order.</summary>
    public IReadOnlyList<string> QueuedKeys
    {
        get
        {
            lock (_queue)
            {
                return _queue.ConvertAll(s => s.Key);
            }
        }
    }

    /// <summary>Every handler invocation, as (replica, step key), in order.</summary>
    public IReadOnlyList<(int Replica, string StepKey)> Executions
    {
        get
        {
            lock (_executions)
            {
                return [.. _executions];
            }
        }
    }

    /// <summary>Opens the gate: the next execution of a <c>Gate</c> step succeeds.</summary>
    public void Signal() => _signalled = true;

    /// <summary>
    /// Starts (or restarts) <paramref name="replica"/> holding <paramref name="flow"/>: syncs its manifest to the
    /// flow store, then runs startup recovery unless <paramref name="recover"/> is <see langword="false"/>.
    /// </summary>
    /// <param name="replica">Replica number.</param>
    /// <param name="flow">The manifest version the replica holds.</param>
    /// <param name="recover">Whether to run <see cref="FlowRunRecoveryHostedService"/> after the sync.</param>
    public async Task StartReplicaAsync(int replica, IFlowDefinition flow, bool recover = true)
    {
        var registry = new FlowRegistry([flow]);
        _manifests[replica] = flow;
        _engines[replica] = CreateEngine(replica, registry);

        if (LosesQueuedStepsOnRestart)
        {
            lock (_queue)
            {
                _queue.Clear();
            }
        }

        var sync = new FlowSyncHostedService(
            registry,
            FlowStore,
            Substitute.For<IRecurringTriggerSync>(),
            new FlowGraphPlanner(),
            NullLogger<FlowSyncHostedService>.Instance);
        await sync.StartAsync(CancellationToken.None);

        if (recover)
        {
            var recovery = new FlowRunRecoveryHostedService(
                Store,
                [Store],
                registry,
                new FlowGraphPlanner(),
                _dispatcher,
                _outputs,
                NullLogger<FlowRunRecoveryHostedService>.Instance);
            await recovery.StartAsync(CancellationToken.None);
        }
    }

    /// <summary>Triggers a run through <paramref name="replica"/> and returns its id.</summary>
    public async Task<Guid> TriggerAsync(int replica)
    {
        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = _manifests[replica],
            Trigger = new Trigger("manual", "Manual", new { order = 1 })
        };

        await _engines[replica].TriggerAsync(ctx);
        return ctx.RunId;
    }

    /// <summary>Executes the queued step <paramref name="stepKey"/> on <paramref name="replica"/>.</summary>
    public Task RunKeyOnAsync(int replica, string stepKey)
    {
        IStepInstance step;
        lock (_queue)
        {
            var index = _queue.FindIndex(s => string.Equals(s.Key, stepKey, StringComparison.Ordinal));
            Assert.True(index >= 0, $"Step '{stepKey}' is not queued. Queued: {string.Join(", ", _queue.ConvertAll(s => s.Key))}.");
            step = _queue[index];
            _queue.RemoveAt(index);
        }

        return ExecuteAsync(replica, step);
    }

    /// <summary>
    /// Executes every step queued right now exactly once on <paramref name="replica"/>; steps queued while
    /// doing so (continuations, hand-backs, re-polls) stay queued.
    /// </summary>
    public async Task RunQueuedOnceOnAsync(int replica)
    {
        List<IStepInstance> snapshot;
        lock (_queue)
        {
            snapshot = [.. _queue];
            _queue.Clear();
        }

        foreach (var step in snapshot)
        {
            await ExecuteAsync(replica, step);
        }
    }

    /// <summary>Executes queued steps on <paramref name="replica"/> until the queue is empty, failing after a bound.</summary>
    public async Task DrainOnAsync(int replica)
    {
        for (var i = 0; i < 200; i++)
        {
            IStepInstance step;
            lock (_queue)
            {
                if (_queue.Count == 0)
                {
                    return;
                }

                step = _queue[0];
                _queue.RemoveAt(0);
            }

            await ExecuteAsync(replica, step);
        }

        Assert.Fail($"Queue did not drain. Queued: {string.Join(", ", QueuedKeys)}.");
    }

    /// <summary>Persisted run status.</summary>
    public async Task<string> RunStatusAsync(Guid runId) => (await Store.GetRunDetailAsync(runId))!.Status;

    /// <summary>Persisted status of every step row of the run, keyed by step key.</summary>
    public async Task<IReadOnlyDictionary<string, string>> StepStatusesAsync(Guid runId)
    {
        var detail = await Store.GetRunDetailAsync(runId);
        return (detail?.Steps ?? []).ToDictionary(s => s.StepKey, s => s.Status, StringComparer.Ordinal);
    }

    /// <summary>Error/reason text of a step row, or <see langword="null"/>.</summary>
    public async Task<string?> StepReasonAsync(Guid runId, string stepKey)
    {
        var detail = await Store.GetRunDetailAsync(runId);
        return detail?.Steps?.FirstOrDefault(s => s.StepKey == stepKey)?.ErrorMessage;
    }

    /// <summary>Builds a flow definition with a fixed id, a manual trigger and the given steps.</summary>
    public static IFlowDefinition Flow(Guid id, string version, StepCollection steps)
        => new DeployFlow(id, version, new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = steps
        });

    /// <summary>Builds a <c>Work</c> step that runs after every key in <paramref name="after"/> succeeded.</summary>
    public static StepMetadata Work(params string[] after) => Step(WorkType, after);

    /// <summary>Builds the signal-gated step that runs after every key in <paramref name="after"/> succeeded.</summary>
    public static StepMetadata Gate(params string[] after) => Step(GateType, after);

    private static StepMetadata Step(string type, string[] after)
    {
        var runAfter = new RunAfterCollection();
        foreach (var key in after)
        {
            runAfter[key] = [StepStatus.Succeeded];
        }

        return new StepMetadata { Type = type, RunAfter = runAfter };
    }

    private ValueTask<string?> Capture(IStepInstance step)
    {
        lock (_queue)
        {
            _queue.Add(step);
        }

        return new ValueTask<string?>("job-" + step.Key);
    }

    private Task ExecuteAsync(int replica, IStepInstance step)
        => _engines[replica].RunStepAsync(new CoreExecutionContext { RunId = step.RunId }, _manifests[replica], step).AsTask();

    private FlowOrchestratorEngine CreateEngine(int replica, FlowRegistry registry)
    {
        var handlers = new IStepHandlerMetadata[]
        {
            new RecordingHandler(WorkType, this, replica, () => StepStatus.Succeeded),
            new RecordingHandler(GateType, this, replica, () => _signalled ? StepStatus.Succeeded : StepStatus.Pending)
        };

        var executor = new DefaultStepExecutor(handlers, Substitute.For<IServiceProvider>(), _outputs, Store);
        return new FlowOrchestratorEngine(
            _dispatcher,
            new FlowGraphPlanner(),
            executor,
            _engineFlowStore,
            Store,
            _outputs,
            Substitute.For<IExecutionContextAccessor>(),
            registry,
            [Store],
            [Store],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            NullLogger<FlowOrchestratorEngine>.Instance,
            NoopFlowEventNotifier.Instance);
    }

    private sealed class DeployFlow(Guid id, string version, FlowManifest manifest) : IFlowDefinition
    {
        public Guid Id { get; } = id;

        public string Version { get; } = version;

        public FlowManifest Manifest { get; set; } = manifest;
    }

    private sealed class RecordingHandler(string type, ManifestDeployHarness owner, int replica, Func<StepStatus> status) : IStepHandlerMetadata
    {
        public string Type { get; } = type;

        public ValueTask<IStepResult> ExecuteAsync(IServiceProvider sp, IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
        {
            lock (owner._executions)
            {
                owner._executions.Add((replica, step.Key));
            }

            var outcome = status();
            return new ValueTask<IStepResult>(new StepResult
            {
                Key = step.Key,
                Status = outcome,
                DelayNextStep = outcome == StepStatus.Pending ? TimeSpan.FromSeconds(1) : null
            });
        }
    }
}
