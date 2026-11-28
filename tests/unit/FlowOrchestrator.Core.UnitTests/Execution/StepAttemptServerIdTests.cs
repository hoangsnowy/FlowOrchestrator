using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// The engine stamps every attempt it starts with this process's server id — the step-lease owner id — through
/// the six-parameter <see cref="IFlowRunStore.RecordStepStartAsync(Guid, string, string, string?, string?, string?)"/>,
/// and a store that only implements the five-parameter overload keeps working.
/// </summary>
public sealed class StepAttemptServerIdTests
{
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly Guid _runId = Guid.NewGuid();

    public StepAttemptServerIdTests()
    {
        _stepExecutor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(call => new ValueTask<IStepResult>(new StepResult { Key = call.ArgAt<IStepInstance>(2).Key }));
    }

    [Fact]
    public async Task RunStepAsync_records_the_lease_owner_as_the_attempt_server_even_with_leases_off()
    {
        // Arrange
        var keeper = new StepLeaseKeeper(new FlowRunControlOptions { StepLeaseDuration = null });
        var engine = CreateEngine(_runStore, keeper);

        // Act
        await engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, Flow("a"), new StepInstance("a", "Work") { RunId = _runId });

        // Assert
        await _runStore.Received(1).RecordStepStartAsync(_runId, "a", "Work", Arg.Any<string?>(), Arg.Any<string?>(), keeper.OwnerId);
    }

    [Fact]
    public async Task RunStepAsync_without_a_keeper_records_no_server()
    {
        // Arrange
        var engine = CreateEngine(_runStore, keeper: null);

        // Act
        await engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, Flow("a"), new StepInstance("a", "Work") { RunId = _runId });

        // Assert
        await _runStore.Received(1).RecordStepStartAsync(_runId, "a", "Work", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Is<string?>(id => id == null));
    }

    [Fact]
    public async Task A_store_implementing_only_the_five_parameter_overload_still_records_the_start()
    {
        // Arrange — FaultInjectingRunStore implements the five-parameter overload only; the six-parameter call
        // reaches it through the interface's default implementation.
        var inner = Substitute.For<IFlowRunStore>();
        var legacy = new FaultInjectingRunStore(inner);
        var engine = CreateEngine(legacy, new StepLeaseKeeper(new FlowRunControlOptions { StepLeaseDuration = null }));

        // Act
        await engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, Flow("a"), new StepInstance("a", "Work") { RunId = _runId });

        // Assert
        await inner.Received(1).RecordStepStartAsync(_runId, "a", "Work", Arg.Any<string?>(), Arg.Any<string?>());
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

    private FlowOrchestratorEngine CreateEngine(IFlowRunStore runStore, StepLeaseKeeper? keeper) =>
        new(
            Substitute.For<IStepDispatcher>(),
            new FlowGraphPlanner(),
            _stepExecutor,
            Substitute.For<IFlowStore>(),
            runStore,
            Substitute.For<IOutputsRepository>(),
            Substitute.For<IExecutionContextAccessor>(),
            FlowRegistry.Empty,
            [EngineTestStores.PermissiveRuntimeStore(
                new Dictionary<string, StepStatus>(StringComparer.Ordinal) { ["a"] = StepStatus.Succeeded })],
            [EngineTestStores.ControlStore()],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            Substitute.For<ILogger<FlowOrchestratorEngine>>(),
            NoopFlowEventNotifier.Instance,
            signalStore: null,
            leaseKeeper: keeper);
}
