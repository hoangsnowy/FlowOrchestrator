using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Tests for the periodic timeout enforcer (<see cref="FlowOrchestratorEngine.EnforceDueTimeoutsAsync"/>),
/// which proactively marks active runs whose deadline has passed as <c>TimedOut</c> so a stuck run does
/// not sit <c>Running</c> indefinitely.
/// </summary>
public sealed class RunTimeoutEnforcementTests
{
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IFlowStore _flowStore = Substitute.For<IFlowStore>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger =
        Substitute.For<ILogger<FlowOrchestratorEngine>>();

    private FlowOrchestratorEngine CreateEngine(InMemoryFlowRunStore store) =>
        new FlowOrchestratorEngine(
            _dispatcher,
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
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger);

    [Fact]
    public async Task EnforceDueTimeoutsAsync_LapsedActiveRun_MarksTimedOutAndCompletesRun()
    {
        // Arrange — active run whose deadline has already passed, nothing in flight.
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("TimedOut", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.NotNull(control!.TimedOutAtUtc);
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_DeadlineNotYetPassed_LeavesRunRunning()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(30));

        // Act
        await engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Running", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_CompletedRun_IsNotTimedOut()
    {
        // Arrange — a Succeeded run whose (irrelevant) deadline has lapsed. Not active → untouched.
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.CompleteRunAsync(runId, "Succeeded");

        // Act
        await engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Succeeded", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_RunWithInFlightStep_LatchesTimeoutButDoesNotComplete()
    {
        // Arrange — lapsed deadline but a step is still Running: the in-flight guard must prevent
        // force-completion. The run stays Running (converges to TimedOut once the step finishes).
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RecordStepStartAsync(runId, "step1", "Work", "{}", "job1");

        // Act
        await engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Running", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.NotNull(control!.TimedOutAtUtc);
    }
}
