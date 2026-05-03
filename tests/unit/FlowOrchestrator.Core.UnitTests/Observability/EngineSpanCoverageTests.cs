using System.Diagnostics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Observability;

/// <summary>Marker collection that serializes activity-source-touching tests; the FlowOrchestrator ActivitySource is process-wide so parallel listeners see each other's events.</summary>
[CollectionDefinition(nameof(FlowActivitySourceCollection), DisableParallelization = true)]
public sealed class FlowActivitySourceCollection
{
}

/// <summary>
/// Tests for the new spans introduced in Phase 3 of the v1.19 observability work:
/// <c>flow.step.retry</c>, <c>flow.step.when</c>, and <c>flow.step.poll</c>.
/// </summary>
[Collection(nameof(FlowActivitySourceCollection))]
public sealed class EngineSpanCoverageTests
{
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowStore _flowStore = Substitute.For<IFlowStore>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger = Substitute.For<ILogger<FlowOrchestratorEngine>>();

    private readonly FlowOrchestratorTelemetry _telemetry = new();

    public EngineSpanCoverageTests()
    {
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));
    }

    private FlowOrchestratorEngine CreateEngine(IFlowRunRuntimeStore? runtimeStore = null) =>
        new(
            _dispatcher,
            _flowExecutor,
            _graphPlanner,
            _stepExecutor,
            _flowStore,
            _runStore,
            _outputsRepo,
            _ctxAccessor,
            _flowRepo,
            runtimeStore is not null ? [runtimeStore] : [],
            [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = true },
            _telemetry,
            _logger);

    private static IFlowDefinition MakeFlow(Guid flowId, string stepKey, RunAfterCollection? runAfter = null)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                [stepKey] = new StepMetadata
                {
                    Type = "Work",
                    RunAfter = runAfter ?? new RunAfterCollection(),
                    Inputs = new Dictionary<string, object?>()
                }
            }
        });
        return flow;
    }

    private static (List<Activity> stopped, ActivityListener listener) StartListener()
    {
        var stopped = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == FlowOrchestratorTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        return (stopped, listener);
    }

    [Fact]
    public async Task RetryStepAsync_StartsFlowStepRetryActivity_AndIncrementsRetriesCounter()
    {
        // Arrange
        var (stopped, listener) = StartListener();
        using var _ = listener;

        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = MakeFlow(flowId, "step1");
        _flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));
        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs(new ValueTask<IStepResult>(new StepResult { Key = "step1", Status = StepStatus.Succeeded }));

        var counterValues = new List<long>();
        using var meterListener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Name == "flow_step_retries") l.EnableMeasurementEvents(instr);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, _, _) => counterValues.Add(value));
        meterListener.Start();

        // Act
        await CreateEngine().RetryStepAsync(flowId, runId, "step1");

        // Assert
        var retryActivity = Assert.Single(stopped, a => a.OperationName == "flow.step.retry");
        Assert.Equal(flowId.ToString(), retryActivity.GetTagItem("flow.id"));
        Assert.Equal(runId.ToString(), retryActivity.GetTagItem("run.id"));
        Assert.Equal("step1", retryActivity.GetTagItem("step.key"));

        Assert.Single(counterValues);
        Assert.Equal(1L, counterValues[0]);
    }

    [Fact]
    public async Task TriggerAsync_WithFalseWhenClause_StartsFlowStepWhenActivity_AndIncrementsSkippedCounter()
    {
        // Arrange
        var (stopped, listener) = StartListener();
        using var _ = listener;

        // Build a flow where step1 has a When that always evaluates false. Note: TryEvaluateWhenAndSkipAsync
        // bails out early when no IFlowRunRuntimeStore is registered, so we must register one and
        // make TryClaimStepAsync succeed.
        var flowId = Guid.NewGuid();
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .ReturnsForAnyArgs(Task.FromResult(true));
        runtimeStore.GetStepStatusesAsync(Arg.Any<Guid>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(
                new Dictionary<string, StepStatus> { ["step1"] = StepStatus.Skipped }));
        runtimeStore.GetClaimedStepKeysAsync(Arg.Any<Guid>())
            .Returns(Task.FromResult<IReadOnlyCollection<string>>(new[] { "step1" }));

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata
                {
                    Type = "Work",
                    // Entry-step-with-When pattern: the synthetic empty-string key marks the
                    // step as an entry while still carrying a When clause (see FlowGraphPlanner.IsEntryStep).
                    RunAfter = new RunAfterCollection
                    {
                        [string.Empty] = new RunAfterCondition { When = "false" }
                    },
                    Inputs = new Dictionary<string, object?>()
                }
            }
        });

        var skippedValues = new List<long>();
        using var meterListener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Name == "flow_step_skipped") l.EnableMeasurementEvents(instr);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, _, _) => skippedValues.Add(value));
        meterListener.Start();

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        // Act
        await CreateEngine(runtimeStore).TriggerAsync(ctx);

        // Assert
        Assert.Single(stopped, a => a.OperationName == "flow.step.when");
        Assert.NotEmpty(skippedValues);
        Assert.Equal(1L, skippedValues[0]);
    }

    [Fact]
    public async Task RunStepAsync_WhenStepReturnsPending_IncrementsPollAttemptsCounter()
    {
        // Arrange
        var (stopped, listener) = StartListener();
        using var _ = listener;

        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs(new ValueTask<IStepResult>(new StepResult
            {
                Key = "step1",
                Status = StepStatus.Pending,
                DelayNextStep = TimeSpan.FromSeconds(1)
            }));

        var pollValues = new List<long>();
        using var meterListener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Name == "flow_step_poll_attempts") l.EnableMeasurementEvents(instr);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, _, _) => pollValues.Add(value));
        meterListener.Start();

        var runId = Guid.NewGuid();
        var flow = MakeFlow(Guid.NewGuid(), "step1");

        // Act
        await CreateEngine().RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        Assert.Single(pollValues);
        Assert.Equal(1L, pollValues[0]);
    }
}
