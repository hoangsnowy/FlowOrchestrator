using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlowOrchestrator.Hangfire.Tests;

public class HangfireFlowOrchestratorTests
{
    private readonly IBackgroundJobClient _jobClient = Substitute.For<IBackgroundJobClient>();
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<HangfireFlowOrchestrator> _logger = Substitute.For<ILogger<HangfireFlowOrchestrator>>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();

    private HangfireFlowOrchestrator CreateSut(IFlowRunRuntimeStore? runtimeStore = null, IFlowRunControlStore? runControlStore = null)
    {
        return new HangfireFlowOrchestrator(
            _jobClient,
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
    }

    private static IFlowDefinition CreateFlow()
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
        return flow;
    }

    [Fact]
    public async Task TriggerAsync_AssignsRunId_WhenEmpty()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        _runStore.StartRunAsync(default, default!, default, default!, default, default)
            .ReturnsForAnyArgs(new FlowRunRecord());

        var ctx = new TriggerContext
        {
            RunId = Guid.Empty,
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        await sut.TriggerAsync(ctx);

        ctx.RunId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task TriggerAsync_StartsRunInStore_AndEnqueuesEntryStep()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var runId = Guid.NewGuid();

        _runStore.StartRunAsync(default, default!, default, default!, default, default)
            .ReturnsForAnyArgs(new FlowRunRecord());

        var ctx = new TriggerContext
        {
            RunId = runId,
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        await sut.TriggerAsync(ctx);

        await _runStore.Received(1).StartRunAsync(
            flow.Id,
            Arg.Any<string>(),
            runId,
            "manual",
            Arg.Any<string?>(),
            Arg.Any<string?>());
        _jobClient.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task TriggerAsync_ClearsContextAccessor()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        _runStore.StartRunAsync(default, default!, default, default!, default, default)
            .ReturnsForAnyArgs(new FlowRunRecord());

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        await sut.TriggerAsync(ctx);

        _ctxAccessor.CurrentContext.Should().BeNull();
    }

    [Fact]
    public async Task RunStepAsync_ExecutesStepAndSavesOutput()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var stepResult = new StepResult { Key = "step1", Status = StepStatus.Succeeded, Result = "ok" };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns((IStepInstance?)null);

        await sut.RunStepAsync(ctx, flow, step);

        await _outputsRepo.Received(1).SaveStepOutputAsync(ctx, flow, step, stepResult);
    }

    [Fact]
    public async Task RunStepAsync_PendingStatus_ReschedulesCurrentStep()
    {
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

        await sut.RunStepAsync(ctx, flow, step);

        await _runStore.Received(1).RecordStepCompleteAsync(
            ctx.RunId, "step1", "Pending", Arg.Any<string?>(), Arg.Any<string?>());
        _jobClient.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunStepAsync_CompletesRunWhenNoNextStep_LegacyPath()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var stepResult = new StepResult { Key = "step1", Status = StepStatus.Succeeded };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns((IStepInstance?)null);

        await sut.RunStepAsync(ctx, flow, step);

        await _runStore.Received(1).CompleteRunAsync(ctx.RunId, "Succeeded");
    }

    [Fact]
    public async Task RunStepAsync_ReThrowTrue_ThrowsException()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        var stepResult = new StepResult { Key = "step1", Status = StepStatus.Failed, ReThrow = true, FailedReason = "critical" };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns((IStepInstance?)null);

        var act = () => sut.RunStepAsync(ctx, flow, step).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("critical");
    }

    [Fact]
    public async Task TriggerAsync_RunStoreFailure_DoesNotThrow()
    {
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

        var act = () => sut.TriggerAsync(ctx).AsTask();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TriggerAsync_IdempotencyDuplicate_ReturnsExistingRun()
    {
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

        var result = await sut.TriggerAsync(ctx);

        ctx.RunId.Should().Be(existingRunId);
        result.Should().NotBeNull();
        _jobClient.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task RunStepAsync_CancelRequested_SkipsStepAndCompletesRun()
    {
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

        await sut.RunStepAsync(ctx, flow, step);

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

        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("step1", "SimulatedFailure") { RunId = runId };

        var failedResult = new StepResult { Key = "step1", Status = StepStatus.Failed, FailedReason = "Input validation failed." };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(failedResult);

        // Both calls to GetStepStatusesAsync return the post-execution state:
        // step1 = Failed, step2 = Skipped (already recorded by a prior worker call).
        IReadOnlyDictionary<string, StepStatus> statuses = new Dictionary<string, StepStatus>
        {
            ["step1"] = StepStatus.Failed,
            ["step2"] = StepStatus.Skipped
        };
        runtimeStore.GetStepStatusesAsync(runId).Returns(statuses);
        // No step is still in-flight and no step is claimed but unrecorded.
        runtimeStore.GetClaimedStepKeysAsync(runId).Returns((IReadOnlyCollection<string>)Array.Empty<string>());
        // TryClaimStepAsync — step2 is already recorded Skipped so graph evaluation won't put it in BlockedStepKeys;
        // but if the planner does try to claim it, return false (already done).
        runtimeStore.TryClaimStepAsync(runId, Arg.Any<string>()).Returns(false);
        runtimeStore.GetRunStatusAsync(runId).Returns((string?)null);

        // Act
        await sut.RunStepAsync(ctx, flow, step);

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
                // step_d is the ONLY leaf — it only runs if step_c fails.
                // Since step_c Succeeded, step_d is Skipped.
                ["step_d"] = new StepMetadata
                {
                    Type = "LogMessage",
                    RunAfter = new RunAfterCollection { ["step_c"] = [StepStatus.Failed] }
                }
            }
        });

        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        // step_c is the step completing now (the last Succeeded step before the leaf check)
        var step = new StepInstance("step_c", "LogMessage") { RunId = runId };

        var succeededResult = new StepResult { Key = "step_c", Status = StepStatus.Succeeded };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(succeededResult);
        _flowExecutor.GetNextStep(ctx, flow, step, succeededResult).Returns((IStepInstance?)null);

        // Post-execution statuses: A, B, C all Succeeded; D already Skipped (blocked).
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
        await sut.RunStepAsync(ctx, flow, step);

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
                // leaf 1 — runs on success (Succeeded)
                ["happy_path"] = new StepMetadata
                {
                    Type = "LogMessage",
                    RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Succeeded] }
                },
                // leaf 2 — only runs on failure (Skipped because start Succeeded)
                ["error_handler"] = new StepMetadata
                {
                    Type = "LogMessage",
                    RunAfter = new RunAfterCollection { ["start"] = [StepStatus.Failed] }
                }
            }
        });

        var runId = Guid.NewGuid();
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("happy_path", "LogMessage") { RunId = runId };

        var succeededResult = new StepResult { Key = "happy_path", Status = StepStatus.Succeeded };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(succeededResult);
        _flowExecutor.GetNextStep(ctx, flow, step, succeededResult).Returns((IStepInstance?)null);

        IReadOnlyDictionary<string, StepStatus> statuses = new Dictionary<string, StepStatus>
        {
            ["start"]         = StepStatus.Succeeded,
            ["happy_path"]    = StepStatus.Succeeded,   // leaf — Succeeded
            ["error_handler"] = StepStatus.Skipped      // leaf — Skipped
        };
        runtimeStore.GetStepStatusesAsync(runId).Returns(statuses);
        runtimeStore.GetClaimedStepKeysAsync(runId).Returns((IReadOnlyCollection<string>)Array.Empty<string>());
        runtimeStore.TryClaimStepAsync(runId, Arg.Any<string>()).Returns(false);
        runtimeStore.GetRunStatusAsync(runId).Returns((string?)null);

        // Act
        await sut.RunStepAsync(ctx, flow, step);

        // Assert: mixed leaves (one Succeeded, one Skipped) → run = Succeeded
        await _runStore.Received(1).CompleteRunAsync(runId, "Succeeded");
    }
}
