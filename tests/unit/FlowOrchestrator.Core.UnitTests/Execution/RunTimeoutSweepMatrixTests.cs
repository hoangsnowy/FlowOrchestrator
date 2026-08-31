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
/// Matrix coverage for <see cref="FlowOrchestratorEngine.EnforceDueTimeoutsAsync"/> across the four
/// shapes an active run can have when its deadline lapses or it is cancelled: a genuinely running
/// step, everything parked, a ForEach barrier parked, and nothing in flight because the continuation
/// never completed the run.
/// </summary>
/// <remarks>
/// Deadlines are set explicitly in the past rather than waited out, so no test here depends on
/// wall-clock timing. The complementary parked-run cases live in <c>ParkedRunForceCloseTests</c> and
/// the barrier-specific ones in <c>LoopBarrierRunControlTests</c>.
/// </remarks>
public sealed class RunTimeoutSweepMatrixTests
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
        new(
            _dispatcher, _flowExecutor, _graphPlanner, _stepExecutor, _flowStore, store,
            _outputsRepo, _ctxAccessor, _flowRepo, [store], [store],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            _logger);

    private static async Task<(InMemoryFlowRunStore Store, Guid RunId)> StartRunAsync(DateTimeOffset? deadline)
    {
        var store = new InMemoryFlowRunStore();
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flowId, "manual", null, deadline);
        return (store, runId);
    }

    [Fact]
    public async Task Sweep_runAlreadyLatchedTimedOutButNeverCompleted_isClosedAsTimedOut()
    {
        // Arrange - the lazy dispatch-time gate latched the timeout, but the step that observed it
        // could not complete the run (something else was in flight at that moment) and nothing has
        // been dispatched since. Pre-fix the sweep skipped every run with TimedOutAtUtc set, so this
        // one stayed Running forever.
        var (store, runId) = await StartRunAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        await store.RecordStepStartAsync(runId, "step1", "Work", null, null);
        await store.RecordStepCompleteAsync(runId, "step1", StepStatus.Skipped.ToString(), null, "Run is TimedOut.");
        await store.MarkTimedOutAsync(runId, "Run timed out before scheduling next step.");

        // Act
        await CreateEngine(store).EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("TimedOut", await store.GetRunStatusAsync(runId));
    }

    [Fact]
    public async Task Sweep_cancelledRunWithNoDeadlineAndNothingInFlight_isClosedAsCancelled()
    {
        // Arrange - cancellation with no run timeout configured at all. Pre-fix the sweep only looked
        // at runs whose TimeoutAtUtc had lapsed, so a cancelled deadline-less run was never closed.
        var (store, runId) = await StartRunAsync(deadline: null);
        await store.RecordStepStartAsync(runId, "step1", "Work", null, null);
        await store.RecordStepCompleteAsync(runId, "step1", StepStatus.Failed.ToString(), null, "boom");
        await store.RequestCancelAsync(runId, "user requested");

        // Act
        await CreateEngine(store).EnforceDueTimeoutsAsync();

        // Assert - Cancelled, and the timeout is NOT latched so a later retry keeps the cancel intent.
        Assert.Equal("Cancelled", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.True(control.CancelRequested);
    }

    [Fact]
    public async Task Sweep_allStepsSucceededButRunNeverClosed_andDeadlineLapsed_reportsTimedOut()
    {
        // Arrange - the host died between persisting the last step result and CompleteRunAsync, and
        // the deadline lapsed while the run sat Running.
        var (store, runId) = await StartRunAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RecordStepStartAsync(runId, "step1", "Work", null, null);
        await store.RecordStepCompleteAsync(runId, "step1", StepStatus.Succeeded.ToString(), "{}", null);

        // Act
        await CreateEngine(store).EnforceDueTimeoutsAsync();

        // Assert - pins a KNOWN DIVERGENCE: the sweep classifies from the control record and reports
        // TimedOut, while FlowRunRecoveryHostedService classifies the same run from its step statuses
        // (RunTerminationClassifier) and would close it Succeeded. Whichever runs first wins.
        Assert.Equal("TimedOut", await store.GetRunStatusAsync(runId));
    }

    [Fact]
    public async Task Sweep_runWithNoControlRecord_isLeftAlone()
    {
        // Arrange - runs created before the control store was wired have no FlowRunControls row.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "TestFlow", runId, "manual", null, null);

        // Act
        await CreateEngine(store).EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Running", await store.GetRunStatusAsync(runId));
    }
}
