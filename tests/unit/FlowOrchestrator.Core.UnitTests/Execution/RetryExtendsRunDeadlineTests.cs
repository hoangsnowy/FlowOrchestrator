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
/// Regression for the "dashboard Retry is a permanent no-op past the timeout deadline" defect:
/// <see cref="FlowOrchestratorEngine.RetryStepAsync"/> must grant the run a fresh execution window
/// (via <see cref="IFlowRunControlStore.ExtendDeadlineAsync"/>) before re-dispatch, otherwise the
/// re-dispatched step is skipped by the termination gate before the handler runs.
/// </summary>
public sealed class RetryExtendsRunDeadlineTests
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

    private FlowOrchestratorEngine CreateEngine(
        InMemoryFlowRunStore store,
        IFlowDefinition flow,
        FlowRunControlOptions options,
        Func<string, IStepResult> resultForStep)
    {
        var dispatcher = Substitute.For<IStepDispatcher>();

        _stepExecutor.ExecuteAsync(
                Arg.Any<IExecutionContext>(),
                Arg.Any<IFlowDefinition>(),
                Arg.Any<IStepInstance>())
            .Returns(call => new ValueTask<IStepResult>(resultForStep(call.Arg<IStepInstance>().Key)));

        _flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));

        return new FlowOrchestratorEngine(
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
            [store],
            options,
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger);
    }

    private static IFlowDefinition MakeSingleStepFlow(Guid flowId)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "Work" }
            }
        });
        return flow;
    }

    [Fact]
    public async Task RetryStepAsync_OnTimedOutRun_ReExecutesHandler_AndRefreshesDeadlineIntoTheFuture()
    {
        // Arrange — step fails on attempt 1 (before deadline), succeeds on retry.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = MakeSingleStepFlow(flowId);
        var attempt = 0;
        var options = new FlowRunControlOptions { DefaultRunTimeout = TimeSpan.FromMinutes(5) };
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store, flow, options, _ => ++attempt == 1
            ? new StepResult { Key = "step1", Status = StepStatus.Failed, FailedReason = "upstream 404" }
            : new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(5));

        // Attempt 1 → Failed, run terminates Failed.
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId }, flow, new StepInstance("step1", "Work") { RunId = runId });
        Assert.Equal("Failed", await store.GetRunStatusAsync(runId));

        // Deadline lapses while the run sits Failed, latching it TimedOut.
        await store.MarkTimedOutAsync(runId, "Deadline exceeded");

        // Act — dashboard Retry.
        await engine.RetryStepAsync(flowId, runId, "step1");

        // Assert — the handler actually ran on retry; the run reaches Succeeded.
        Assert.Equal(2, attempt);
        Assert.Equal(StepStatus.Succeeded, (await store.GetStepStatusesAsync(runId))["step1"]);
        Assert.Equal("Succeeded", await store.GetRunStatusAsync(runId));

        // Assert — the timeout latch is cleared and the deadline is refreshed into the future.
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.False(control.CancelRequested);
        Assert.NotNull(control.TimeoutAtUtc);
        Assert.True(control.TimeoutAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RetryStepAsync_OnTimedOutRun_WithTimeoutDisabled_StillReExecutesHandler()
    {
        // Arrange — DefaultRunTimeout null (timeout globally disabled), no per-run override.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = MakeSingleStepFlow(flowId);
        var attempt = 0;
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store, flow, new FlowRunControlOptions(), _ => ++attempt == 1
            ? new StepResult { Key = "step1", Status = StepStatus.Failed, FailedReason = "boom" }
            : new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, null);

        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId }, flow, new StepInstance("step1", "Work") { RunId = runId });
        await store.MarkTimedOutAsync(runId, "Deadline exceeded");

        // Act
        await engine.RetryStepAsync(flowId, runId, "step1");

        // Assert — handler ran; run Succeeded; no timeout bound restored.
        Assert.Equal("Succeeded", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.Null(control.TimeoutAtUtc);
    }

    [Fact]
    public async Task RefreshedDeadline_StillBoundsARunawayLoop()
    {
        // Arrange — a refreshed execution window that has itself already lapsed (a genuinely
        // runaway retry loop). The termination gate must still time the run out — the refresh
        // must never disable the protection the timeout exists for.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = MakeSingleStepFlow(flowId);
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store, flow, new FlowRunControlOptions(),
            _ => new StepResult { Key = "step1", Status = StepStatus.Succeeded });

        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, null);
        await store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act — dispatch the step against the lapsed refreshed deadline.
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId }, flow, new StepInstance("step1", "Work") { RunId = runId });

        // Assert — the handler never ran; the run is timed out again.
        await _stepExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!);
        Assert.Equal("TimedOut", await store.GetRunStatusAsync(runId));
    }
}
