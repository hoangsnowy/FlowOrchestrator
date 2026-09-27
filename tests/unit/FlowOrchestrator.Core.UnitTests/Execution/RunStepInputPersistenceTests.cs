using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Covers two per-step changes from the #189 backlog in <see cref="FlowOrchestratorEngine.RunStepAsync"/>:
/// the engine no longer writes the step's unresolved inputs to <see cref="IOutputsRepository"/> (the
/// executor overwrites that row with the resolved inputs anyway), and a step handed over without its
/// trigger payload gets it back from the rehydrated context.
/// </summary>
public sealed class RunStepInputPersistenceTests
{
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IOutputsRepository _outputs = Substitute.For<IOutputsRepository>();
    private readonly Guid _runId = Guid.NewGuid();

    /// <summary>Wires a succeeding executor and the minimal run-store responses the step path reads.</summary>
    public RunStepInputPersistenceTests()
    {
        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(call => new ValueTask<IStepResult>(new StepResult { Key = call.ArgAt<IStepInstance>(2).Key }));
    }

    [Fact]
    public async Task RunStepAsync_does_not_persist_the_unresolved_inputs_itself()
    {
        // Arrange
        var engine = CreateEngine();
        var step = new StepInstance("a", "Work") { RunId = _runId, Inputs = new Dictionary<string, object?> { ["x"] = "@triggerBody().x" } };

        // Act
        await engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, Flow("a"), step);

        // Assert — the executor owns the "{key}:input" row; the unresolved form is still recorded on
        // the step row via RecordStepStartAsync.
        await _outputs.DidNotReceive().SaveStepInputAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>());
        await _runStore.Received(1).RecordStepStartAsync(_runId, "a", "Work", Arg.Is<string?>(json => json!.Contains("@triggerBody().x")), Arg.Any<string?>());
    }

    [Fact]
    public async Task RunStepAsync_rehydrates_the_trigger_payload_onto_a_slim_step()
    {
        // Arrange — the InMemory delayed schedule and the Service Bus envelope both hand over a step
        // without TriggerData / TriggerHeaders; a handler reading them off the step must still see them.
        var payload = new { orderId = "ORD-1" };
        var headers = new Dictionary<string, string> { ["X-Request-Id"] = "r-1" };
        _outputs.GetTriggerDataAsync(_runId).Returns(new ValueTask<object?>(payload));
        _outputs.GetTriggerHeadersAsync(_runId).Returns(new ValueTask<IReadOnlyDictionary<string, string>?>(headers));
        IStepInstance? executed = null;
        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Do<IStepInstance>(s => executed = s))
            .Returns(new ValueTask<IStepResult>(new StepResult { Key = "a" }));
        var engine = CreateEngine();
        var slimStep = new StepInstance("a", "Work") { RunId = _runId };

        // Act
        await engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, Flow("a"), slimStep);

        // Assert
        Assert.NotNull(executed);
        Assert.Same(payload, executed!.TriggerData);
        Assert.Same(headers, executed.TriggerHeaders);
    }

    [Fact]
    public async Task RunStepAsync_keeps_a_trigger_payload_the_step_already_carries()
    {
        // Arrange
        var own = new { source = "step" };
        _outputs.GetTriggerDataAsync(_runId).Returns(new ValueTask<object?>(new { source = "repo" }));
        var engine = CreateEngine();
        var step = new StepInstance("a", "Work") { RunId = _runId, TriggerData = own };

        // Act
        await engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, Flow("a"), step);

        // Assert — rehydration only fills a gap; it never overwrites.
        Assert.Same(own, step.TriggerData);
    }

    private static IFlowDefinition Flow(string stepKey)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { [stepKey] = new StepMetadata { Type = "Work" } }
        });
        return flow;
    }

    private FlowOrchestratorEngine CreateEngine() =>
        new(
            Substitute.For<IStepDispatcher>(),
            Substitute.For<IFlowExecutor>(),
            new FlowGraphPlanner(),
            _stepExecutor,
            Substitute.For<IFlowStore>(),
            _runStore,
            _outputs,
            Substitute.For<IExecutionContextAccessor>(),
            Substitute.For<IFlowRepository>(),
            [],
            [],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            Substitute.For<ILogger<FlowOrchestratorEngine>>());
}
