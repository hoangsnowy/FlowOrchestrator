using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Tests for the core execution invariants of <see cref="FlowOrchestratorEngine"/>,
/// validating the "Dispatch many, Execute once" contract enforced by the two-layer
/// guard: dispatch ledger (<see cref="IFlowRunStore.TryRecordDispatchAsync"/>) and
/// runtime claim (<see cref="IFlowRunRuntimeStore.TryClaimStepAsync"/>).
/// </summary>
public sealed class FlowOrchestratorEngineInvariantTests
{
    // ── Substitutes ───────────────────────────────────────────────────────────

    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger =
        Substitute.For<ILogger<FlowOrchestratorEngine>>();

    public FlowOrchestratorEngineInvariantTests()
    {
        // Allow dispatch to proceed in all tests by default.
        // Individual tests override this to exercise the "already dispatched" guard.
        _runStore.TryRecordDispatchAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));
    }

    // ── Factory helpers ───────────────────────────────────────────────────────

    private FlowOrchestratorEngine CreateEngine(
        IFlowRunRuntimeStore? runtimeStore = null,
        IFlowRunStore? runStoreOverride = null,
        IFlowRunControlStore? runControlStore = null) =>
        new FlowOrchestratorEngine(
            _dispatcher,
            _flowExecutor,
            _graphPlanner,
            _stepExecutor,
            runStoreOverride ?? _runStore,
            _outputsRepo,
            _ctxAccessor,
            _flowRepo,
            runtimeStore is not null ? [runtimeStore] : [],
            runControlStore is not null ? [runControlStore] : [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger);

    private static IFlowDefinition MakeFlow(params string[] stepKeys)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var steps = new StepCollection();
        foreach (var key in stepKeys)
            steps[key] = new StepMetadata { Type = "Work" };
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private static ITriggerContext MakeTriggerCtx(IFlowDefinition flow, Guid? runId = null) =>
        new TriggerContext
        {
            RunId = runId ?? Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

    private static IExecutionContext MakeCtx(Guid runId) =>
        new CoreExecutionContext { RunId = runId };

    // ── Invariant 1: Dispatch is idempotent ───────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenTryRecordDispatchReturnsFalse_DispatcherIsNeverCalled()
    {
        // Override default: the dispatch ledger reports every step as already dispatched.
        _runStore.TryRecordDispatchAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(false));

        var ctx = MakeTriggerCtx(MakeFlow("step1"));

        await CreateEngine().TriggerAsync(ctx);

        _dispatcher.ReceivedCalls().Should().BeEmpty(
            "TryRecordDispatch returned false — the step is already in the dispatch ledger, " +
            "so the runtime must not receive a second enqueue");
    }

    // ── Invariant 2: Claim exclusion ──────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenTryClaimStepReturnsFalse_DispatcherIsNeverCalled()
    {
        // A runtime store that always refuses the claim — simulates another worker
        // already owning this step during a parallel fan-out.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .ReturnsForAnyArgs(Task.FromResult(false));

        var ctx = MakeTriggerCtx(MakeFlow("step1"));

        await CreateEngine(runtimeStore: runtimeStore).TriggerAsync(ctx);

        _dispatcher.ReceivedCalls().Should().BeEmpty(
            "TryClaimStep returned false — another worker already owns this step, " +
            "so the dispatcher must not be called");
    }

    // ── Invariant 3: Polling rescheduling order ───────────────────────────────

    [Fact]
    public async Task RunStepAsync_WhenPending_ReleasesDispatchBeforeSchedulingNextAttempt()
    {
        // Verify ordering: ReleaseDispatchAsync must fire before ScheduleStepAsync.
        var releaseWasCalled = false;
        var scheduleObservedReleaseFirst = false;

        _runStore.ReleaseDispatchAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                releaseWasCalled = true;
                return Task.CompletedTask;
            });

        _dispatcher.ScheduleStepAsync(
                Arg.Any<IExecutionContext>(),
                Arg.Any<IFlowDefinition>(),
                Arg.Any<IStepInstance>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                scheduleObservedReleaseFirst = releaseWasCalled;
                return new ValueTask<string?>("job-rescheduled");
            });

        _stepExecutor.ExecuteAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs(new ValueTask<IStepResult>(new StepResult
            {
                Key = "step1",
                Status = StepStatus.Pending,
                DelayNextStep = TimeSpan.FromSeconds(5)
            }));

        var runId = Guid.NewGuid();
        var flow = MakeFlow("step1");

        await CreateEngine().RunStepAsync(
            MakeCtx(runId), flow, new StepInstance("step1", "Work") { RunId = runId });

        await _runStore.Received(1)
            .ReleaseDispatchAsync(runId, "step1", Arg.Any<CancellationToken>());
        await _dispatcher.Received(1).ScheduleStepAsync(
            Arg.Any<IExecutionContext>(),
            flow,
            Arg.Is<IStepInstance>(s => s.Key == "step1"),
            Arg.Is<TimeSpan>(d => d == TimeSpan.FromSeconds(5)),
            Arg.Any<CancellationToken>());
        scheduleObservedReleaseFirst.Should().BeTrue(
            "ReleaseDispatchAsync must be called before ScheduleStepAsync so the next poll " +
            "attempt can be atomically re-recorded in the dispatch ledger");
    }

    // ── Invariant 6: DispatchHint targeting static DAG step throws ────────────

    [Fact]
    public async Task RunStepAsync_DispatchHintTargetingStaticStep_ThrowsInvalidOperationException()
    {
        // The executor returns a hint spawning "step2", which already exists in the manifest.
        var flow = MakeFlow("step1", "step2");
        var runId = Guid.NewGuid();

        _stepExecutor.ExecuteAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs(new ValueTask<IStepResult>(new StepResult
            {
                Key = "step1",
                Status = StepStatus.Succeeded,
                DispatchHint = new StepDispatchHint(Spawn: [
                    new StepDispatchRequest("step2", "Work", new Dictionary<string, object?>())
                ])
            }));

        var act = async () =>
            await CreateEngine().RunStepAsync(
                MakeCtx(runId), flow, new StepInstance("step1", "Work") { RunId = runId });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*static DAG*");
    }

    // ── Invariant 4: Cascade skip → run completes Skipped ─────────────────────

    [Fact]
    public async Task RunStepAsync_WhenAllLeafStepsAreSkipped_CompletesRunWithSkippedStatus()
    {
        // Flow: step1 (entry, Succeeded) + step2 (only runs if step1 Failed).
        // When step1 Succeeds, step2 is blocked by the graph → engine records it as Skipped.
        // Since step2 is the only leaf and it is Skipped, the run must complete as Skipped.
        var realStore = new InMemoryFlowRunStore();
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "Work" },
                ["step2"] = new StepMetadata
                {
                    Type = "Fallback",
                    RunAfter = new RunAfterCollection
                    {
                        ["step1"] = [StepStatus.Failed]   // only runs on failure
                    }
                }
            }
        });

        await realStore.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);

        _stepExecutor.ExecuteAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs(new ValueTask<IStepResult>(new StepResult
            {
                Key = "step1",
                Status = StepStatus.Succeeded
            }));

        var engine = CreateEngine(runtimeStore: realStore, runStoreOverride: realStore);

        await engine.RunStepAsync(
            MakeCtx(runId), flow, new StepInstance("step1", "Work") { RunId = runId });

        var status = await realStore.GetRunStatusAsync(runId);
        status.Should().Be(StepStatus.Skipped.ToString(),
            "all leaf steps were skipped because their runAfter conditions could not be satisfied — " +
            "the run should complete as Skipped, not Succeeded");
    }
}
