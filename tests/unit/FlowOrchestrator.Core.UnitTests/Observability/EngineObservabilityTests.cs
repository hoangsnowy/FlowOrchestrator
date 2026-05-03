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

/// <summary>
/// Phase 1 of the v1.19 production-grade observability work: validates the engine marks
/// activities as <see cref="ActivityStatusCode.Error"/> on failure and opens a logger scope
/// carrying <c>RunId</c> / <c>FlowId</c> / <c>StepKey</c> for every entry point.
/// </summary>
[Collection(nameof(FlowActivitySourceCollection))]
public sealed class EngineObservabilityTests
{
    // ── Substitutes shared across tests ───────────────────────────────────────

    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowStore _flowStore = Substitute.For<IFlowStore>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();

    public EngineObservabilityTests()
    {
        _runStore.TryRecordDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(true));
    }

    private FlowOrchestratorEngine CreateEngine(ILogger<FlowOrchestratorEngine> logger, FlowOrchestratorTelemetry? telemetry = null) =>
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
            [],
            [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = true },
            telemetry ?? new FlowOrchestratorTelemetry(),
            logger);

    private static IFlowDefinition MakeFlow(string stepKey)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var steps = new StepCollection
        {
            [stepKey] = new StepMetadata { Type = "Work" }
        };
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private static ITriggerContext MakeTriggerCtx(IFlowDefinition flow) =>
        new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

    // ── Activity error recording ──────────────────────────────────────────────

    [Fact]
    public async Task RunStepAsync_WhenHandlerThrows_RecordsExceptionAndSetsActivityStatusError()
    {
        // Arrange
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == FlowOrchestratorTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stoppedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var bang = new InvalidOperationException("boom");
        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs<ValueTask<IStepResult>>(_ => throw bang);

        var logger = Substitute.For<ILogger<FlowOrchestratorEngine>>();
        var telemetry = new FlowOrchestratorTelemetry();
        var engine = CreateEngine(logger, telemetry);

        var runId = Guid.NewGuid();
        var flow = MakeFlow("step1");
        var step = new StepInstance("step1", "Work") { RunId = runId };

        // Act
        await engine.RunStepAsync(new CoreExecutionContext { RunId = runId }, flow, step);

        // Assert
        var stepActivity = stoppedActivities.SingleOrDefault(a => a.OperationName == "flow.step");
        Assert.NotNull(stepActivity);
        Assert.Equal(ActivityStatusCode.Error, stepActivity!.Status);
        var exceptionEvent = Assert.Single(stepActivity.Events, e => e.Name == "exception");
        var tagDict = exceptionEvent.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(typeof(InvalidOperationException).FullName, tagDict["exception.type"]);
        Assert.Equal("boom", tagDict["exception.message"]);
    }

    [Fact]
    public async Task TriggerAsync_WhenEngineThrows_RecordsExceptionOnTriggerActivity()
    {
        // Arrange
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == FlowOrchestratorTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stoppedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var logger = Substitute.For<ILogger<FlowOrchestratorEngine>>();
        var telemetry = new FlowOrchestratorTelemetry();
        var engine = CreateEngine(logger, telemetry);

        // A flow with no steps causes TriggerAsync to throw "No entry step found".
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = new StepCollection() });
        var ctx = MakeTriggerCtx(flow);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.TriggerAsync(ctx));

        // Assert
        var triggerActivity = stoppedActivities.SingleOrDefault(a => a.OperationName == "flow.trigger");
        Assert.NotNull(triggerActivity);
        Assert.Equal(ActivityStatusCode.Error, triggerActivity!.Status);
        Assert.Single(triggerActivity.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task RunStepAsync_WhenHandlerSucceeds_DoesNotMarkActivityError()
    {
        // Arrange
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == FlowOrchestratorTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stoppedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs(new ValueTask<IStepResult>(new StepResult
            {
                Key = "step1",
                Status = StepStatus.Succeeded
            }));

        var logger = Substitute.For<ILogger<FlowOrchestratorEngine>>();
        var engine = CreateEngine(logger);

        var runId = Guid.NewGuid();
        var flow = MakeFlow("step1");

        // Act
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        var stepActivity = stoppedActivities.SingleOrDefault(a => a.OperationName == "flow.step");
        Assert.NotNull(stepActivity);
        Assert.NotEqual(ActivityStatusCode.Error, stepActivity!.Status);
        Assert.DoesNotContain(stepActivity.Events, e => e.Name == "exception");
    }

    // ── Logger scope correlation ──────────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_OpensLogScopeWithRunIdAndFlowId()
    {
        // Arrange
        var logger = new RecordingLogger<FlowOrchestratorEngine>();
        var engine = CreateEngine(logger);
        var flow = MakeFlow("step1");
        var ctx = MakeTriggerCtx(flow);

        // Act
        await engine.TriggerAsync(ctx);

        // Assert
        Assert.Contains(logger.Scopes, s =>
            s.ContainsKey("RunId") && s["RunId"]?.Equals(ctx.RunId) == true &&
            s.ContainsKey("FlowId") && s["FlowId"]?.Equals(flow.Id) == true);
    }

    [Fact]
    public async Task RunStepAsync_OpensLogScopeWithRunIdFlowIdAndStepKey()
    {
        // Arrange
        var logger = new RecordingLogger<FlowOrchestratorEngine>();
        var engine = CreateEngine(logger);

        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .ReturnsForAnyArgs(new ValueTask<IStepResult>(new StepResult
            {
                Key = "step1",
                Status = StepStatus.Succeeded
            }));

        var runId = Guid.NewGuid();
        var flow = MakeFlow("step1");

        // Act
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        Assert.Contains(logger.Scopes, s =>
            s.ContainsKey("RunId") && s["RunId"]?.Equals(runId) == true &&
            s.ContainsKey("FlowId") && s["FlowId"]?.Equals(flow.Id) == true &&
            s.ContainsKey("StepKey") && (string?)s["StepKey"] == "step1");
    }

    // ── Test logger that records every BeginScope state ───────────────────────

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                Scopes.Add(kvps.ToDictionary(k => k.Key, v => v.Value));
            }
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Intentionally no-op — these tests assert on scopes, not log lines.
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
