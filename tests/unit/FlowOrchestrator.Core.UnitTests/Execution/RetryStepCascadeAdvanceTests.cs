using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Regression for the manual-retry → DAG-advance gap: when a step's failure caused the
/// continuation to eagerly mark downstream blocked steps as <see cref="StepStatus.Skipped"/>
/// (<see cref="StepSkipReasons.PrerequisitesUnmet"/>), a successful manual retry of the
/// failed step must clear those cascade-skip records and re-evaluate the DAG so the
/// dependents actually run. Pre-fix the retry only reset the failed step itself — its
/// downstream stayed Skipped forever.
/// </summary>
public sealed class RetryStepCascadeAdvanceTests
{
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowStore _flowStore = Substitute.For<IFlowStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger =
        Substitute.For<ILogger<FlowOrchestratorEngine>>();

    private static IFlowDefinition MakeChainedFlow(Guid id, params string[] stepKeys)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        var steps = new StepCollection();
        for (var i = 0; i < stepKeys.Length; i++)
        {
            var key = stepKeys[i];
            var meta = new StepMetadata { Type = "Work" };
            if (i > 0)
            {
                meta.RunAfter[stepKeys[i - 1]] = [StepStatus.Succeeded];
            }
            steps[key] = meta;
        }
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    /// <summary>
    /// Self-draining test rig: the substitute dispatcher captures every dispatch call into
    /// a queue; <see cref="DrainAsync"/> pulls each captured step off the queue and invokes
    /// the engine inline. Mirrors the InMemory runtime "dispatch → execute" loop without
    /// scheduling delays or timing.
    /// </summary>
    private sealed class TestRig
    {
        public required FlowOrchestratorEngine Engine { get; init; }
        public required InMemoryFlowRunStore Store { get; init; }
        public required Queue<IStepInstance> PendingDispatch { get; init; }
        public required List<string> EnqueuedKeys { get; init; }

        public async Task DrainAsync(IFlowDefinition flow)
        {
            while (PendingDispatch.Count > 0)
            {
                var step = PendingDispatch.Dequeue();
                await Engine.RunStepAsync(
                    new CoreExecutionContext { RunId = step.RunId },
                    flow,
                    step);
            }
        }
    }

    private TestRig BuildRig(IFlowDefinition flow, Func<string, IStepResult> resultForStep)
    {
        var store = new InMemoryFlowRunStore();
        var dispatcher = Substitute.For<IStepDispatcher>();
        var pending = new Queue<IStepInstance>();
        var enqueued = new List<string>();

        ValueTask<string?> Capture(NSubstitute.Core.CallInfo call)
        {
            var step = call.Arg<IStepInstance>();
            enqueued.Add(step.Key);
            pending.Enqueue(step);
            return new ValueTask<string?>("job-" + step.Key);
        }

        dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(),
                Arg.Any<IFlowDefinition>(),
                Arg.Any<IStepInstance>(),
                Arg.Any<CancellationToken>())
            .Returns(Capture);

        dispatcher.ScheduleStepAsync(
                Arg.Any<IExecutionContext>(),
                Arg.Any<IFlowDefinition>(),
                Arg.Any<IStepInstance>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Capture);

        _stepExecutor.ExecuteAsync(
                Arg.Any<IExecutionContext>(),
                Arg.Any<IFlowDefinition>(),
                Arg.Any<IStepInstance>())
            .Returns(call =>
            {
                var step = call.Arg<IStepInstance>();
                return new ValueTask<IStepResult>(resultForStep(step.Key));
            });

        _flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));

        var engine = new FlowOrchestratorEngine(
            dispatcher,
            _flowExecutor,
            _graphPlanner,
            _stepExecutor,
            _flowStore,
            store,
            _outputsRepo,
            _ctxAccessor,
            _flowRepo,
            [store],
            [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger);

        return new TestRig
        {
            Engine = engine,
            Store = store,
            PendingDispatch = pending,
            EnqueuedKeys = enqueued
        };
    }

    [Fact]
    public async Task RetryStepAsync_AfterDownstreamCascadeSkip_ReDispatchesDirectDependent()
    {
        // Arrange — A → B → C; A succeeds, B fails on first attempt and succeeds on retry.
        var flow = MakeChainedFlow(Guid.NewGuid(), "a", "b", "c");
        var runId = Guid.NewGuid();
        var bAttempt = 0;

        var rig = BuildRig(flow, key => key switch
        {
            "b" => ++bAttempt == 1
                ? new StepResult { Key = "b", Status = StepStatus.Failed, FailedReason = "boom" }
                : new StepResult { Key = "b", Status = StepStatus.Succeeded },
            _ => new StepResult { Key = key, Status = StepStatus.Succeeded }
        });

        await rig.Store.StartRunAsync(flow.Id, "ChainFlow", runId, "manual", null, null);

        // Act 1 — run A; the continuation enqueues B → drain runs B → B fails →
        // continuation cascade-skips C → run terminates Failed.
        await rig.Engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("a", "Work") { RunId = runId });
        await rig.DrainAsync(flow);

        var statusesAfterFailure = await rig.Store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Succeeded, statusesAfterFailure["a"]);
        Assert.Equal(StepStatus.Failed, statusesAfterFailure["b"]);
        Assert.Equal(StepStatus.Skipped, statusesAfterFailure["c"]);
        Assert.Equal("Failed", await rig.Store.GetRunStatusAsync(runId));

        rig.EnqueuedKeys.Clear();

        // Act 2 — manually retry B; the cascade-skip on C must be cleared so that B's
        // post-retry continuation re-evaluates C and dispatches it.
        await rig.Engine.RetryStepAsync(flow.Id, runId, "b");
        await rig.DrainAsync(flow);

        // Assert — C dispatched and ran; the run is now Succeeded.
        var finalStatuses = await rig.Store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Succeeded, finalStatuses["b"]);
        Assert.Equal(StepStatus.Succeeded, finalStatuses["c"]);
        Assert.Equal("Succeeded", await rig.Store.GetRunStatusAsync(runId));
        Assert.Contains("c", rig.EnqueuedKeys);
    }

    [Fact]
    public async Task RetryStepAsync_AfterDownstreamCascadeSkip_RecursivelyAdvancesAllDescendants()
    {
        // Arrange — A → B → C → D; B fails initially, succeeds on retry. The continuation
        // only propagates skip ONE level (C is marked Skipped); D stays unscheduled (absent
        // from the status map) because its prerequisite C is not yet final at the moment
        // the eager-skip loop runs. After the retry of B, the natural per-step continuation
        // chain should advance: B → C → D. The fix must:
        //   1. Clear C's cascade-skip record (direct dependent of B), and
        //   2. Let C's own post-completion continuation dispatch D (transitive dependent).
        var flow = MakeChainedFlow(Guid.NewGuid(), "a", "b", "c", "d");
        var runId = Guid.NewGuid();
        var bAttempt = 0;

        var rig = BuildRig(flow, key => key switch
        {
            "b" => ++bAttempt == 1
                ? new StepResult { Key = "b", Status = StepStatus.Failed, FailedReason = "boom" }
                : new StepResult { Key = "b", Status = StepStatus.Succeeded },
            _ => new StepResult { Key = key, Status = StepStatus.Succeeded }
        });

        await rig.Store.StartRunAsync(flow.Id, "ChainFlow", runId, "manual", null, null);

        // Act 1 — initial run; B fails → C cascade-skipped (direct dependent), D stays
        // absent from the status map → run terminates Failed.
        await rig.Engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("a", "Work") { RunId = runId });
        await rig.DrainAsync(flow);

        var statusesAfterFailure = await rig.Store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Succeeded, statusesAfterFailure["a"]);
        Assert.Equal(StepStatus.Failed, statusesAfterFailure["b"]);
        Assert.Equal(StepStatus.Skipped, statusesAfterFailure["c"]);
        Assert.False(statusesAfterFailure.ContainsKey("d"));
        Assert.Equal("Failed", await rig.Store.GetRunStatusAsync(runId));

        rig.EnqueuedKeys.Clear();

        // Act 2 — retry B. Reset clears C (direct cascade-skipped descendant). D was never
        // recorded so nothing to reset there; once C succeeds the engine will dispatch D
        // through the normal continuation path.
        await rig.Engine.RetryStepAsync(flow.Id, runId, "b");
        await rig.DrainAsync(flow);

        // Assert — every step ran in sequence, final run status is Succeeded.
        var finalStatuses = await rig.Store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Succeeded, finalStatuses["b"]);
        Assert.Equal(StepStatus.Succeeded, finalStatuses["c"]);
        Assert.Equal(StepStatus.Succeeded, finalStatuses["d"]);
        Assert.Equal("Succeeded", await rig.Store.GetRunStatusAsync(runId));
        Assert.Contains("c", rig.EnqueuedKeys);
        Assert.Contains("d", rig.EnqueuedKeys);
    }

    [Fact]
    public async Task RetryStepAsync_DoesNotResetSkippedStepsBearingDifferentReason()
    {
        // Arrange — flow A→B→C; B fails causing C to be cascade-skipped. Inject a separate
        // step Z marked Skipped with a non-sentinel reason (simulating a When-clause skip).
        // After retrying B, only C (sentinel reason) is cleared; Z must remain Skipped.
        var flow = MakeChainedFlow(Guid.NewGuid(), "a", "b", "c");
        var runId = Guid.NewGuid();
        var bAttempt = 0;

        var rig = BuildRig(flow, key => key switch
        {
            "b" => ++bAttempt == 1
                ? new StepResult { Key = "b", Status = StepStatus.Failed, FailedReason = "boom" }
                : new StepResult { Key = "b", Status = StepStatus.Succeeded },
            _ => new StepResult { Key = key, Status = StepStatus.Succeeded }
        });

        await rig.Store.StartRunAsync(flow.Id, "ChainFlow", runId, "manual", null, null);

        await rig.Engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("a", "Work") { RunId = runId });
        await rig.DrainAsync(flow);

        var afterFailure = await rig.Store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Skipped, afterFailure["c"]);

        // Inject a When-clause-style skip for an unrelated step key.
        await ((IFlowRunRuntimeStore)rig.Store).RecordSkippedStepAsync(
            runId, "z", "Work", "When clause 'x == 1' evaluated to false (0).");

        // Act
        await rig.Engine.RetryStepAsync(flow.Id, runId, "b");
        await rig.DrainAsync(flow);

        // Assert — C cleared and now Succeeded; Z untouched.
        var final = await rig.Store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Succeeded, final["c"]);
        Assert.Equal(StepStatus.Skipped, final["z"]);
    }
}
