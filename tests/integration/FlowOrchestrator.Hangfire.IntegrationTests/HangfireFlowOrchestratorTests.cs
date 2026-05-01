using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Hangfire;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlowOrchestrator.Hangfire.Tests;

public class HangfireFlowOrchestratorTests
{
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowRunStore _runStore;
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger = Substitute.For<ILogger<FlowOrchestratorEngine>>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();

    public HangfireFlowOrchestratorTests()
    {
        _runStore = Substitute.For<IFlowRunStore>();
        // Phase 5: TryRecordDispatchAsync must return true by default so dispatch proceeds in all tests.
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));
    }

    private HangfireFlowOrchestrator CreateSut(IFlowRunRuntimeStore? runtimeStore = null, IFlowRunControlStore? runControlStore = null)
    {
        var engine = new FlowOrchestratorEngine(
            _dispatcher,
            _flowExecutor,
            _graphPlanner,
            _stepExecutor,
            _runStore,
            _outputsRepo,
            _ctxAccessor,
            _flowRepo,
            runtimeStore is not null ? [runtimeStore] : [],
            runControlStore is not null ? [runControlStore] : [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger);
        return new HangfireFlowOrchestrator(engine, _flowRepo);
    }

    private IFlowDefinition CreateFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "LogMessage" }
            }
        });
        // Register with the IFlowRepository substitute so HangfireFlowOrchestrator can rehydrate it from flow.Id.
        RegisterFlowWithRepo(flow);
        return flow;
    }

    private readonly List<IFlowDefinition> _registeredFlows = new();

    private void RegisterFlowWithRepo(IFlowDefinition flow)
    {
        if (!_registeredFlows.Any(f => f.Id == flow.Id))
        {
            _registeredFlows.Add(flow);
        }
        IReadOnlyList<IFlowDefinition> snapshot = _registeredFlows.ToList();
        _flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(snapshot));
    }

    [Fact]
    public async Task TriggerAsync_AssignsRunId_WhenEmpty()
    {
        // Arrange
        var sut = CreateSut();
        var flow = CreateFlow();
        _runStore.StartRunAsync(default, default!, default, default!, default, default, default)
            .ReturnsForAnyArgs(new FlowRunRecord());

        var ctx = new TriggerContext
        {
            RunId = Guid.Empty,
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        // Act
        await sut.TriggerAsync(ctx);

        // Assert
        Assert.NotEqual(Guid.Empty, ctx.RunId);
    }

    [Fact]
    public async Task TriggerAsync_StartsRunInStore_AndEnqueuesEntryStep()
    {
        // Arrange
        var sut = CreateSut();
        var flow = CreateFlow();
        var runId = Guid.NewGuid();

        _runStore.StartRunAsync(default, default!, default, default!, default, default, default)
            .ReturnsForAnyArgs(new FlowRunRecord());

        var ctx = new TriggerContext
        {
            RunId = runId,
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        // Act
        await sut.TriggerAsync(ctx);

        // Assert
        await _runStore.Received(1).StartRunAsync(
            flow.Id,
            Arg.Any<string>(),
            runId,
            "manual",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<Guid?>());
        Assert.NotEmpty(_dispatcher.ReceivedCalls());
    }

    [Fact]
    public async Task TriggerAsync_ClearsContextAccessor()
    {
        // Arrange
        var sut = CreateSut();
        var flow = CreateFlow();
        _runStore.StartRunAsync(default, default!, default, default!, default, default, default)
            .ReturnsForAnyArgs(new FlowRunRecord());

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        // Act
        await sut.TriggerAsync(ctx);

        // Assert
        Assert.Null(_ctxAccessor.CurrentContext);
    }

    [Fact]
    public async Task RunStepAsync_ExecutesStepAndSavesOutput()
    {
        // Arrange
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var stepResult = new StepResult { Key = "step1", Status = StepStatus.Succeeded, Result = "ok" };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns((IStepInstance?)null);

        // Act
        await sut.RunStepAsync(ctx, flow.Id, step);

        // Assert
        await _outputsRepo.Received(1).SaveStepOutputAsync(ctx, flow, step, stepResult);
    }

    [Fact]
    public async Task RunStepAsync_PendingStatus_ReschedulesCurrentStep()
    {
        // Arrange
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var stepResult = new StepResult
        {
            Key = "step1",
            Status = StepStatus.Pending,
            DelayNextStep = TimeSpan.FromSeconds(3)
        };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);

        // Act
        await sut.RunStepAsync(ctx, flow.Id, step);

        // Assert
        await _runStore.Received(1).RecordStepCompleteAsync(
            ctx.RunId, "step1", "Pending", Arg.Any<string?>(), Arg.Any<string?>());
        Assert.NotEmpty(_dispatcher.ReceivedCalls());
    }

    [Fact]
    public async Task RunStepAsync_CompletesRunWhenNoNextStep_LegacyPath()
    {
        // Arrange
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var stepResult = new StepResult { Key = "step1", Status = StepStatus.Succeeded };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns((IStepInstance?)null);

        // Act
        await sut.RunStepAsync(ctx, flow.Id, step);

        // Assert
        await _runStore.Received(1).CompleteRunAsync(ctx.RunId, "Succeeded");
    }

    [Fact]
    public async Task RunStepAsync_ReThrowTrue_ThrowsException()
    {
        // Arrange
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var stepResult = new StepResult { Key = "step1", Status = StepStatus.Failed, ReThrow = true, FailedReason = "critical" };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns((IStepInstance?)null);

        // Act
        var act = () => sut.RunStepAsync(ctx, flow.Id, step).AsTask();

        // Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("critical", ex.Message);
    }

    [Fact]
    public async Task TriggerAsync_RunStoreFailure_DoesNotThrow()
    {
        // Arrange
        var sut = CreateSut();
        var flow = CreateFlow();

        _runStore.StartRunAsync(default, default!, default, default!, default, default)
            .ThrowsAsyncForAnyArgs(new Exception("DB down"));

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        // Act
        var ex = await Record.ExceptionAsync(() => sut.TriggerAsync(ctx).AsTask());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task TriggerAsync_IdempotencyDuplicate_ReturnsExistingRun()
    {
        // Arrange
        var controlStore = Substitute.For<IFlowRunControlStore>();
        var existingRunId = Guid.NewGuid();
        controlStore.FindRunIdByIdempotencyKeyAsync(Arg.Any<Guid>(), "manual", "dup-key")
            .Returns(existingRunId);

        var sut = CreateSut(runControlStore: controlStore);
        var flow = CreateFlow();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Idempotency-Key"] = "dup-key"
        };
        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null, headers)
        };

        // Act
        var result = await sut.TriggerAsync(ctx);

        // Assert
        Assert.Equal(existingRunId, ctx.RunId);
        Assert.NotNull(result);
        Assert.Empty(_dispatcher.ReceivedCalls());
    }

    [Fact]
    public async Task RunStepAsync_CancelRequested_SkipsStepAndCompletesRun()
    {
        // Arrange
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.GetStepStatusesAsync(Arg.Any<Guid>())
            .Returns(new Dictionary<string, StepStatus>());
        runtimeStore.GetClaimedStepKeysAsync(Arg.Any<Guid>())
            .Returns(Array.Empty<string>());

        var controlStore = Substitute.For<IFlowRunControlStore>();
        controlStore.GetRunControlAsync(Arg.Any<Guid>())
            .Returns(new FlowRunControlRecord { RunId = Guid.NewGuid(), CancelRequested = true });

        var sut = CreateSut(runtimeStore, controlStore);
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        // Act
        await sut.RunStepAsync(ctx, flow.Id, step);

        // Assert
        await _runStore.Received(1).CompleteRunAsync(ctx.RunId, "Cancelled");
    }

    [Fact]
    public async Task RunStepAsync_RunStatusIsFailed_WhenFailedStepHasBlockedDownstream()
    {
        // Arrange: two-step flow where step1 fails and step2 is already Skipped (Blocked).
        // The run-level status must be "Failed", not "Skipped" — a real step crash should not
        // be masked by downstream skip propagation.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        var sut = CreateSut(runtimeStore: runtimeStore);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "SimulatedFailure" },
                ["step2"] = new StepMetadata
                {
                    Type = "LogMessage",
                    // step2 only runs after step1 Succeeded — so it becomes Blocked when step1 fails
                    RunAfter = new RunAfterCollection { ["step1"] = [StepStatus.Succeeded] }
                }
            }
        });
        RegisterFlowWithRepo(flow);

        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("step1", "SimulatedFailure") { RunId = runId };

        var failedResult = new StepResult { Key = "step1", Status = StepStatus.Failed, FailedReason = "Input validation failed." };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(failedResult);

        IReadOnlyDictionary<string, StepStatus> statuses = new Dictionary<string, StepStatus>
        {
            ["step1"] = StepStatus.Failed,
            ["step2"] = StepStatus.Skipped
        };
        runtimeStore.GetStepStatusesAsync(runId).Returns(statuses);
        runtimeStore.GetClaimedStepKeysAsync(runId).Returns((IReadOnlyCollection<string>)Array.Empty<string>());
        runtimeStore.TryClaimStepAsync(runId, Arg.Any<string>()).Returns(false);
        runtimeStore.GetRunStatusAsync(runId).Returns((string?)null);

        // Act
        await sut.RunStepAsync(ctx, flow.Id, step);

        // Assert: run must complete as Failed, not Skipped
        await _runStore.Received(1).CompleteRunAsync(runId, "Failed");
    }

    [Fact]
    public async Task RunStepAsync_RunStatusIsSkipped_WhenAllLeafStepsAreSkipped()
    {
        // Arrange: linear chain A→B→C→D where D only fires on C's failure.
        // C succeeds → D is Skipped. D is the only leaf (no step depends on it).
        // ALL leaf steps are Skipped → run-level status must be "Skipped", not "Succeeded".
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        var sut = CreateSut(runtimeStore: runtimeStore);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step_a"] = new StepMetadata { Type = "LogMessage" },
                ["step_b"] = new StepMetadata
                {
                    Type = "LogMessage",
                    RunAfter = new RunAfterCollection { ["step_a"] = [StepStatus.Succeeded] }
                },
                ["step_c"] = new StepMetadata
                {
                    Type = "LogMessage",
                    RunAfter = new RunAfterCollection { ["step_b"] = [StepStatus.Succeeded] }
                },
                ["step_d"] = new StepMetadata
                {
                    Type = "LogMessage",
                    RunAfter = new RunAfterCollection { ["step_c"] = [StepStatus.Failed] }
                }
            }
        });
        RegisterFlowWithRepo(flow);

        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("step_c", "LogMessage") { RunId = runId };

        var succeededResult = new StepResult { Key = "step_c", Status = StepStatus.Succeeded };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(succeededResult);
        _flowExecutor.GetNextStep(ctx, flow, step, succeededResult).Returns((IStepInstance?)null);

        IReadOnlyDictionary<string, StepStatus> statuses = new Dictionary<string, StepStatus>
        {
            ["step_a"] = StepStatus.Succeeded,
            ["step_b"] = StepStatus.Succeeded,
            ["step_c"] = StepStatus.Succeeded,
            ["step_d"] = StepStatus.Skipped
        };
        runtimeStore.GetStepStatusesAsync(runId).Returns(statuses);
        runtimeStore.GetClaimedStepKeysAsync(runId).Returns((IReadOnlyCollection<string>)Array.Empty<string>());
        runtimeStore.TryClaimStepAsync(runId, Arg.Any<string>()).Returns(false);
        runtimeStore.GetRunStatusAsync(runId).Returns((string?)null);

        // Act
        await sut.RunStepAsync(ctx, flow.Id, step);

        // Assert: all leaves Skipped → run = Skipped, not Succeeded
        await _runStore.Received(1).CompleteRunAsync(runId, "Skipped");
    }

    [Fact]
    public async Task RunStepAsync_RunStatusIsSucceeded_WhenSomeLeafSucceededAndSomeSkipped()
    {
        // Arrange: flow with two leaves — one Succeeded, one Skipped.
        // Rule: only ALL-leaves-skipped triggers run-level Skipped.
        // Mixed leaves → run = Succeeded.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        var sut = CreateSut(runtimeStore: runtimeStore);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["start"] = new StepMetadata { Type = "LogMessage" },
                ["happy_path"] = new StepMetadata
                {
                    Type = "LogMessage",
                    RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Succeeded] }
                },
                ["error_handler"] = new StepMetadata
                {
                    Type = "LogMessage",
                    RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Failed] }
                }
            }
        });
        RegisterFlowWithRepo(flow);

        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("happy_path", "LogMessage") { RunId = runId };

        var succeededResult = new StepResult { Key = "happy_path", Status = StepStatus.Succeeded };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(succeededResult);
        _flowExecutor.GetNextStep(ctx, flow, step, succeededResult).Returns((IStepInstance?)null);

        IReadOnlyDictionary<string, StepStatus> statuses = new Dictionary<string, StepStatus>
        {
            ["start"]         = StepStatus.Succeeded,
            ["happy_path"]    = StepStatus.Succeeded,
            ["error_handler"] = StepStatus.Skipped
        };
        runtimeStore.GetStepStatusesAsync(runId).Returns(statuses);
        runtimeStore.GetClaimedStepKeysAsync(runId).Returns((IReadOnlyCollection<string>)Array.Empty<string>());
        runtimeStore.TryClaimStepAsync(runId, Arg.Any<string>()).Returns(false);
        runtimeStore.GetRunStatusAsync(runId).Returns((string?)null);

        // Act
        await sut.RunStepAsync(ctx, flow.Id, step);

        // Assert: mixed leaves (one Succeeded, one Skipped) → run = Succeeded
        await _runStore.Received(1).CompleteRunAsync(runId, "Succeeded");
    }

    [Fact]
    public async Task RunStepAsync_WithDispatchHint_Spawn_DispatchesChildren()
    {
        // Arrange: step returns a DispatchHint with two dynamic children (ForEach-style fan-out).
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("loop1", "ForEach") { RunId = ctx.RunId };

        var hint = new StepDispatchHint(
        [
            new StepDispatchRequest("loop1.0.child", "DoWork", new Dictionary<string, object?> { ["__loopIndex"] = 0 }),
            new StepDispatchRequest("loop1.1.child", "DoWork", new Dictionary<string, object?> { ["__loopIndex"] = 1 }, TimeSpan.FromMilliseconds(100))
        ]);

        var stepResult = new StepResult { Key = "loop1", Status = StepStatus.Succeeded, DispatchHint = hint };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns((IStepInstance?)null);

        await sut.RunStepAsync(ctx, flow.Id, step);

        // Should dispatch one immediate + one delayed child
        var calls = _dispatcher.ReceivedCalls().ToList();
        Assert.Equal(2, calls.Count);
        Assert.True(calls.Any(c => c.GetMethodInfo().Name == nameof(IStepDispatcher.EnqueueStepAsync)));
        Assert.True(calls.Any(c => c.GetMethodInfo().Name == nameof(IStepDispatcher.ScheduleStepAsync)));
    }

    [Fact]
    public async Task RunStepAsync_WithDispatchHint_TargetingStaticDagStep_Throws()
    {
        // Arrange: hint targets "step1" which exists in the static DAG — this is illegal.
        var sut = CreateSut();
        var flow = CreateFlow();   // CreateFlow() has "step1" in its manifest
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("loop1", "ForEach") { RunId = ctx.RunId };

        var hint = new StepDispatchHint(
        [
            new StepDispatchRequest("step1", "LogMessage", new Dictionary<string, object?>())
        ]);

        var stepResult = new StepResult { Key = "loop1", Status = StepStatus.Succeeded, DispatchHint = hint };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);

        var act = () => sut.RunStepAsync(ctx, flow.Id, step).AsTask();

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("static DAG", ex.Message);
        Assert.Contains("step1", ex.Message);
    }
}
