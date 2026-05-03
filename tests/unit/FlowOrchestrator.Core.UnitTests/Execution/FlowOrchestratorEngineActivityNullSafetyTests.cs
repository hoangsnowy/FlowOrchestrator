using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Regression coverage for the four <c>activity.RecordError(...)</c> call sites in
/// <see cref="FlowOrchestratorEngine"/> that were dereferencing a nullable
/// <see cref="System.Diagnostics.Activity"/>. With <c>EnableOpenTelemetry = false</c>
/// the activity is never created and an exception thrown inside the protected block must
/// not turn into a <see cref="NullReferenceException"/>.
/// </summary>
public sealed class FlowOrchestratorEngineActivityNullSafetyTests
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

    private FlowOrchestratorEngine CreateEngineOtelDisabled() =>
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

    [Fact]
    public async Task TriggerAsync_WhenInnerCodeThrows_NoNullReferenceFromActivityRecordError()
    {
        // Arrange — drive into the catch block by throwing from SaveTriggerDataAsync.
        var flow = MakeFlow("step1");
        _outputsRepo.SaveTriggerDataAsync(Arg.Any<ITriggerContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<ITrigger>())
            .Throws(new InvalidOperationException("boom from outputs repo"));

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        // Act
        var ex = await Record.ExceptionAsync(async () => await CreateEngineOtelDisabled().TriggerAsync(ctx));

        // Assert — the originating exception propagates, but no NRE from a null-deref.
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("boom", ex!.Message);
    }

    [Fact]
    public async Task RunStepAsync_HandlerFails_NoNullReferenceFromActivityRecordError()
    {
        // Arrange — engine without OTel; force the inner step-executor catch by throwing.
        var flow = MakeFlow("step1");
        var runId = Guid.NewGuid();
        var step = new StepInstance("step1", "Work") { RunId = runId };
        var ctx = new CoreExecutionContext { RunId = runId };

        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Throws(new InvalidOperationException("handler boom"));

        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.TryClaimStepAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(Task.FromResult(true));

        var engine = new FlowOrchestratorEngine(
            _dispatcher,
            _flowExecutor,
            _graphPlanner,
            _stepExecutor,
            _flowStore,
            _runStore,
            _outputsRepo,
            _ctxAccessor,
            _flowRepo,
            [runtimeStore],
            [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger);

        // Act
        var ex = await Record.ExceptionAsync(async () => await engine.RunStepAsync(ctx, flow, step));

        // Assert — handler exception is swallowed and translated into Failed StepResult.
        // The catch path that previously dereferenced a null Activity must not throw NRE.
        Assert.Null(ex);
    }

    [Fact]
    public async Task TriggerAsync_DisabledFlow_DoesNotThrowWhenOpenTelemetryIsOff()
    {
        // Arrange — capture the substituted Id so `Returns(...)` does not collide with
        // NSubstitute's own pending-spec for `flow.Id`.
        var flow = MakeFlow("step1");
        var flowId = flow.Id;
        _flowStore.GetByIdAsync(flowId)
            .Returns(new FlowDefinitionRecord { Id = flowId, Name = "Test", Version = "1.0", IsEnabled = false });

        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger("manual", "Manual", null)
        };

        // Act
        var result = await CreateEngineOtelDisabled().TriggerAsync(ctx);

        // Assert — silent skip with disabled = true. No NRE despite OTel being off.
        Assert.NotNull(result);
        var disabledProp = result!.GetType().GetProperty("disabled")?.GetValue(result);
        Assert.Equal(true, disabledProp);
    }
}
