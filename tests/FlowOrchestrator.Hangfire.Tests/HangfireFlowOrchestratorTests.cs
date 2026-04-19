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
}
