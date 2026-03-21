using FlowOrchestrator.Core.Abstractions;
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

    private HangfireFlowOrchestrator CreateSut() =>
        new(_jobClient, _flowExecutor, _stepExecutor, _runStore, _outputsRepo, _ctxAccessor, _flowRepo, _logger);

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
        var firstStep = new StepInstance("step1", "LogMessage") { RunId = Guid.NewGuid() };

        _flowExecutor.TriggerFlow(Arg.Any<ITriggerContext>()).Returns(firstStep);
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
    public async Task TriggerAsync_StartsRunInStore()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var runId = Guid.NewGuid();
        var firstStep = new StepInstance("step1", "LogMessage") { RunId = runId };

        _flowExecutor.TriggerFlow(Arg.Any<ITriggerContext>()).Returns(firstStep);
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
    }

    [Fact]
    public async Task TriggerAsync_EnqueuesFirstStep()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var firstStep = new StepInstance("step1", "LogMessage") { RunId = Guid.NewGuid() };

        _flowExecutor.TriggerFlow(Arg.Any<ITriggerContext>()).Returns(firstStep);
        _runStore.StartRunAsync(default, default!, default, default!, default, default)
            .ReturnsForAnyArgs(new FlowRunRecord());

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        await sut.TriggerAsync(ctx);

        _jobClient.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task TriggerAsync_PopulatesTriggerDataOnContext()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var firstStep = new StepInstance("step1", "LogMessage") { RunId = Guid.NewGuid() };
        var payload = new { orderId = "ORD-1" };

        _flowExecutor.TriggerFlow(Arg.Any<ITriggerContext>()).Returns(firstStep);
        _runStore.StartRunAsync(default, default!, default, default!, default, default)
            .ReturnsForAnyArgs(new FlowRunRecord());

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", payload)
        };

        await sut.TriggerAsync(ctx);

        ctx.TriggerData.Should().BeSameAs(payload);
        await _flowExecutor.Received(1).TriggerFlow(
            Arg.Is<ITriggerContext>(c => ReferenceEquals(c.TriggerData, payload)));
    }

    [Fact]
    public async Task TriggerAsync_ClearsContextAccessor()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var firstStep = new StepInstance("step1", "LogMessage") { RunId = Guid.NewGuid() };

        _flowExecutor.TriggerFlow(Arg.Any<ITriggerContext>()).Returns(firstStep);
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
    public async Task RunStepAsync_LoadsTriggerDataFromRepository_WhenMissingOnContext()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var runId = Guid.NewGuid();
        var triggerData = new { orderId = "123" };
        var ctx = new Core.Execution.ExecutionContext { RunId = runId };
        var step = new StepInstance("step1", "LogMessage") { RunId = runId };
        IExecutionContext? capturedContext = null;

        _outputsRepo.GetTriggerDataAsync(runId).Returns(triggerData);

        var stepResult = new StepResult { Key = "step1", Status = StepStatus.Succeeded };
        _stepExecutor.ExecuteAsync(Arg.Do<IExecutionContext>(c => capturedContext = c), flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns((IStepInstance?)null);

        await sut.RunStepAsync(ctx, flow, step);

        await _outputsRepo.Received(1).GetTriggerDataAsync(runId);
        capturedContext.Should().NotBeNull();
        capturedContext!.TriggerData.Should().BeSameAs(triggerData);
    }

    [Fact]
    public async Task RunStepAsync_CompletesRunWhenNoNextStep()
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
    public async Task RunStepAsync_EnqueuesNextStep()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "A") { RunId = ctx.RunId };
        var nextStep = new StepInstance("step2", "B") { RunId = ctx.RunId };

        var stepResult = new StepResult { Key = "step1", Status = StepStatus.Succeeded };
        _stepExecutor.ExecuteAsync(ctx, flow, step).Returns(stepResult);
        _flowExecutor.GetNextStep(ctx, flow, step, stepResult).Returns(nextStep);

        await sut.RunStepAsync(ctx, flow, step);

        await _runStore.DidNotReceive().CompleteRunAsync(Arg.Any<Guid>(), Arg.Any<string>());
        _jobClient.ReceivedCalls().Should().NotBeEmpty();
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
        await _flowExecutor.DidNotReceive().GetNextStep(
            Arg.Any<IExecutionContext>(),
            Arg.Any<IFlowDefinition>(),
            Arg.Any<IStepInstance>(),
            Arg.Any<IStepResult>());
        await _runStore.DidNotReceive().CompleteRunAsync(Arg.Any<Guid>(), Arg.Any<string>());
        _jobClient.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunStepAsync_StepExecutionFailure_RecordsFailedStatus()
    {
        var sut = CreateSut();
        var flow = CreateFlow();
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var step = new StepInstance("step1", "LogMessage") { RunId = ctx.RunId };

        _stepExecutor.ExecuteAsync(ctx, flow, step).Throws(new InvalidOperationException("boom"));
        _flowExecutor.GetNextStep(ctx, flow, step, Arg.Any<IStepResult>()).Returns((IStepInstance?)null);

        await sut.RunStepAsync(ctx, flow, step);

        await _runStore.Received(1).RecordStepCompleteAsync(
            ctx.RunId, "step1", "Failed", Arg.Any<string?>(), Arg.Any<string?>());
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
        var firstStep = new StepInstance("step1", "LogMessage") { RunId = Guid.NewGuid() };

        _flowExecutor.TriggerFlow(Arg.Any<ITriggerContext>()).Returns(firstStep);
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
}
