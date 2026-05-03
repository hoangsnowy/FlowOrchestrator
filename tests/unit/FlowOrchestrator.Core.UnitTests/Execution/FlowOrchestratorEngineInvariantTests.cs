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
    private readonly IFlowStore _flowStore = Substitute.For<IFlowStore>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger =
        Substitute.For<ILogger<FlowOrchestratorEngine>>();

    public FlowOrchestratorEngineInvariantTests()
    {
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
            _flowStore,
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
        // Arrange
        _runStore.TryRecordDispatchAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(false));
        var ctx = MakeTriggerCtx(MakeFlow("step1"));

        // Act
        await CreateEngine().TriggerAsync(ctx);

        // Assert
        Assert.Empty(_dispatcher.ReceivedCalls());
    }

    // ── Invariant 2: Claim exclusion (v1.22+ at execute time, not schedule time) ──

    [Fact]
    public async Task RunStepAsync_WhenTryClaimStepReturnsFalse_StepExecutorIsNeverCalled()
    {
        // Arrange — pre-1.22 the claim was at schedule time so this test verified that no
        // dispatch happened. Post-1.22 the claim moved to RunStepAsync entry; we now verify the
        // claim losers exit silently and never invoke IStepExecutor (the heavy bit).
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .ReturnsForAnyArgs(Task.FromResult(false));
        var flow = MakeFlow("step1");
        var runId = Guid.NewGuid();
        var ctx = MakeCtx(runId);
        var step = new StepInstance("step1", "Work") { RunId = runId };

        // Act
        await CreateEngine(runtimeStore: runtimeStore).RunStepAsync(ctx, flow, step);

        // Assert — claim lost ⇒ executor not called, no step.started event recorded.
        Assert.Empty(_stepExecutor.ReceivedCalls());
    }

    [Fact]
    public async Task TriggerAsync_DoesNotConsultRuntimeClaim_PerV122ScheduleSemantics()
    {
        // Arrange — explicit pin: schedule path no longer touches the runtime claim. The claim
        // belongs to RunStepAsync execution. This test guards against accidentally re-introducing
        // schedule-time claiming, which would re-break broadcast delivery scenarios.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .ReturnsForAnyArgs(Task.FromResult(true));
        var ctx = MakeTriggerCtx(MakeFlow("step1"));

        // Act
        await CreateEngine(runtimeStore: runtimeStore).TriggerAsync(ctx);

        // Assert
        await runtimeStore.DidNotReceiveWithAnyArgs().TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    // ── Invariant 3: Polling rescheduling order ───────────────────────────────

    [Fact]
    public async Task RunStepAsync_WhenPending_ReleasesDispatchBeforeSchedulingNextAttempt()
    {
        // Arrange
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

        // Act
        await CreateEngine().RunStepAsync(
            MakeCtx(runId), flow, new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        await _runStore.Received(1)
            .ReleaseDispatchAsync(runId, "step1", Arg.Any<CancellationToken>());
        await _dispatcher.Received(1).ScheduleStepAsync(
            Arg.Any<IExecutionContext>(),
            flow,
            Arg.Is<IStepInstance>(s => s.Key == "step1"),
            Arg.Is<TimeSpan>(d => d == TimeSpan.FromSeconds(5)),
            Arg.Any<CancellationToken>());
        Assert.True(scheduleObservedReleaseFirst);
    }

    [Fact]
    public async Task RunStepAsync_WhenPending_AlsoReleasesStepClaim_v122()
    {
        // Arrange — pre-1.22 the claim leaked across Pending re-schedules and the next poll
        // attempt couldn't re-claim. Post-1.22 the engine releases the claim alongside the
        // dispatch ledger so the polling loop actually progresses.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .ReturnsForAnyArgs(Task.FromResult(true));

        _stepExecutor.ExecuteAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs(new ValueTask<IStepResult>(new StepResult
            {
                Key = "step1",
                Status = StepStatus.Pending,
                DelayNextStep = TimeSpan.FromSeconds(2),
            }));

        var runId = Guid.NewGuid();
        var flow = MakeFlow("step1");

        // Act
        await CreateEngine(runtimeStore: runtimeStore).RunStepAsync(
            MakeCtx(runId), flow, new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        await runtimeStore.Received(1).ReleaseStepClaimAsync(runId, "step1");
    }

    // ── Invariant 6: DispatchHint targeting static DAG step throws ────────────

    [Fact]
    public async Task RunStepAsync_DispatchHintTargetingStaticStep_ThrowsInvalidOperationException()
    {
        // Arrange
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

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("static DAG", ex.Message);
    }

    // ── Invariant 4: Cascade skip → run completes Skipped ─────────────────────

    [Fact]
    public async Task RunStepAsync_WhenAllLeafStepsAreSkipped_CompletesRunWithSkippedStatus()
    {
        // Arrange
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
                        ["step1"] = [StepStatus.Failed]
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

        // Act
        await engine.RunStepAsync(
            MakeCtx(runId), flow, new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        var status = await realStore.GetRunStatusAsync(runId);
        Assert.Equal("Skipped", status);
    }

    // ── Invariant 5: Disabled flow rejection ──────────────────────────────────
    // Engine must silently reject TriggerAsync for a flow whose store record is IsEnabled=false.
    // This is the runtime-agnostic gate that closes the gap discovered when designing the
    // ServiceBus runtime (per-flow subscriptions can leak in-flight messages); putting the
    // check at the engine layer covers Hangfire / InMemory / ServiceBus in one place.

    [Fact]
    public async Task TriggerAsync_WhenFlowIsDisabled_ReturnsDisabledMarkerWithoutDispatching()
    {
        // Arrange — use a hand-rolled IFlowStore stub instead of NSubstitute because
        // Task<T?> return-type matching trips NSubstitute when stubbing concrete records.
        var flow = MakeFlow("step1");
        var disabledStore = new SingleRecordFlowStore(
            new FlowDefinitionRecord { Id = flow.Id, IsEnabled = false });
        var engine = CreateEngineWithStore(disabledStore);
        var ctx = MakeTriggerCtx(flow);

        // Act
        var result = await engine.TriggerAsync(ctx);

        // Assert — silent skip: no dispatch, no run record, response shape carries `disabled=true`.
        Assert.Empty(_dispatcher.ReceivedCalls());
        await _runStore.DidNotReceiveWithAnyArgs().StartRunAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<Guid?>());
        var json = System.Text.Json.JsonSerializer.SerializeToNode(result);
        Assert.NotNull(json);
        Assert.Equal(true, json!["disabled"]?.GetValue<bool>());
        Assert.Null(json!["runId"]?.GetValue<Guid?>());
    }

    [Fact]
    public async Task TriggerAsync_WhenFlowIsEnabled_DispatchesNormally()
    {
        // Arrange
        var flow = MakeFlow("step1");
        var enabledStore = new SingleRecordFlowStore(
            new FlowDefinitionRecord { Id = flow.Id, IsEnabled = true });
        var engine = CreateEngineWithStore(enabledStore);
        var ctx = MakeTriggerCtx(flow);

        // Act
        await engine.TriggerAsync(ctx);

        // Assert
        await _dispatcher.ReceivedWithAnyArgs().EnqueueStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
            Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_WhenFlowStoreReturnsNull_DispatchesNormally()
    {
        // Arrange — flow not yet in IFlowStore (e.g. first trigger before FlowSyncHostedService
        // has run, or a code-only flow in tests). Falling back to "enabled" is the safe default.
        var flow = MakeFlow("step1");
        var emptyStore = new SingleRecordFlowStore(record: null);
        var engine = CreateEngineWithStore(emptyStore);
        var ctx = MakeTriggerCtx(flow);

        // Act
        await engine.TriggerAsync(ctx);

        // Assert
        await _dispatcher.ReceivedWithAnyArgs().EnqueueStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
            Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>());
    }

    private FlowOrchestratorEngine CreateEngineWithStore(IFlowStore store) =>
        new FlowOrchestratorEngine(
            _dispatcher,
            _flowExecutor,
            _graphPlanner,
            _stepExecutor,
            store,
            _runStore,
            _outputsRepo,
            _ctxAccessor,
            _flowRepo,
            [],
            [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger);

    private sealed class SingleRecordFlowStore : IFlowStore
    {
        private readonly FlowDefinitionRecord? _record;
        public SingleRecordFlowStore(FlowDefinitionRecord? record) => _record = record;
        public Task<IReadOnlyList<FlowDefinitionRecord>> GetAllAsync() =>
            Task.FromResult<IReadOnlyList<FlowDefinitionRecord>>(_record is null ? Array.Empty<FlowDefinitionRecord>() : new[] { _record });
        public Task<FlowDefinitionRecord?> GetByIdAsync(Guid id) =>
            Task.FromResult(_record?.Id == id ? _record : null);
        public Task<FlowDefinitionRecord> SaveAsync(FlowDefinitionRecord record) => Task.FromResult(record);
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<FlowDefinitionRecord> SetEnabledAsync(Guid id, bool enabled) =>
            Task.FromResult(_record ?? new FlowDefinitionRecord { Id = id, IsEnabled = enabled });
    }
}
