using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
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

    private FlowOrchestratorEngine CreateEngine(InMemoryFlowRunStore store, IFlowEventNotifier? notifier = null) =>
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
            _logger,
            notifier);

    /// <summary>Counts published <see cref="RunCompletedEvent"/>s so tests can assert exactly-once completion.</summary>
    private sealed class CountingNotifier : IFlowEventNotifier
    {
        private int _runCompleted;
        public int RunCompletedCount => _runCompleted;

        public ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default)
        {
            if (evt is RunCompletedEvent)
            {
                Interlocked.Increment(ref _runCompleted);
            }
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_StuckRunningRunWithFailedStep_MarksTimedOutAndCompletesRun()
    {
        // Arrange — the production scenario: a step failed and left nothing scheduled, so the run sat
        // Running past its deadline. No in-flight work remains → the sweep must complete it TimedOut.
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RecordStepStartAsync(runId, "step1", "Work", "{}", "job1");
        await store.RecordStepCompleteAsync(runId, "step1", "Failed", null, "upstream 404");

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
    public async Task EnforceDueTimeoutsAsync_RunWithInFlightStep_IsLeftUntouched()
    {
        // Arrange — lapsed deadline but a step is still Running: the run is NOT stuck. The periodic
        // sweep must leave it entirely alone (no latch, no completion) so the dispatch-time gate can
        // enforce the deadline on the step's next dispatch. Latching here would risk an inconsistent
        // TimedOut(control)/Succeeded(run) state if the final in-flight step then completed.
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RecordStepStartAsync(runId, "step1", "Work", "{}", "job1");

        // Act
        await engine.EnforceDueTimeoutsAsync();

        // Assert — untouched: still Running, deadline not latched.
        Assert.Equal("Running", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_StuckLapsedUserCancelledRun_CompletesAsCancelled_PreservingCancel()
    {
        // Arrange — a user cancelled a run that then got stuck past its deadline. The sweep must
        // finalise it as Cancelled (honouring intent), NOT mislabel it TimedOut, and must NOT latch
        // the timeout — otherwise a later retry would clear the cancel.
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RequestCancelAsync(runId, "user requested");

        // Act
        await engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Cancelled", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.True(control.CancelRequested);
        Assert.Equal("user requested", control.CancelReason);
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_CalledTwiceOnStuckRun_PublishesExactlyOneRunCompletedEvent()
    {
        // Arrange — a stuck, lapsed run. Two sweeps (mimicking a second replica / a re-entrant tick)
        // must complete the run exactly once, thanks to the guarded CompleteRunIfActiveAsync.
        var notifier = new CountingNotifier();
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store, notifier);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await engine.EnforceDueTimeoutsAsync();
        await engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("TimedOut", await store.GetRunStatusAsync(runId));
        Assert.Equal(1, notifier.RunCompletedCount);
    }
}
