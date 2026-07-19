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
/// Tests for run-level control features: idempotency key deduplication,
/// cancellation enforcement, and timeout enforcement.
/// </summary>
public sealed class RunControlTests
{
    // ── Substitutes ───────────────────────────────────────────────────────────

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

    // ── Factory helpers ───────────────────────────────────────────────────────

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

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_DuplicateIdempotencyKey_ReturnsSameRunIdWithoutStartingNewRun()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var flow = MakeSingleStepFlow(flowId);
        var idempotencyKey = Guid.NewGuid().ToString();

        var firstCtx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger(
                "manual", "Manual", null,
                headers: new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey })
        };

        // Act
        await engine.TriggerAsync(firstCtx);
        var firstRunId = firstCtx.RunId;
        Assert.NotEqual(Guid.Empty, firstRunId);

        var secondCtx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = flow,
            Trigger = new Trigger(
                "manual", "Manual", null,
                headers: new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey })
        };

        await engine.TriggerAsync(secondCtx);

        // Assert
        Assert.Equal(firstRunId, secondCtx.RunId);
        var runs = await store.GetRunsAsync(flowId: flowId);
        Assert.Single(runs);
    }

    [Fact]
    public async Task RunStepAsync_WhenCancellationRequested_SkipsStepAndCompletesRunAsCancelled()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = MakeSingleStepFlow(flowId);

        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        // Configure the run-control record first, mirroring what the engine's TriggerAsync does.
        // RequestCancelAsync updates an existing control record (and returns false if none exists),
        // so the record must be present before the cancel request is recorded.
        await store.ConfigureRunAsync(runId, flowId, "manual", null, null);
        await store.RequestCancelAsync(runId, "Test cancellation");

        // Act
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        await _stepExecutor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default!);
        var status = await store.GetRunStatusAsync(runId);
        Assert.Equal("Cancelled", status);
    }

    [Fact]
    public async Task RunStepAsync_WhenRunTimedOut_SkipsStepAndCompletesRunAsTimedOut()
    {
        // Arrange
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = MakeSingleStepFlow(flowId);

        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        // Configure the run-control record first, mirroring what the engine's TriggerAsync does.
        // MarkTimedOutAsync updates an existing control record (and returns false if none exists),
        // so the record must be present before the timeout is recorded.
        await store.ConfigureRunAsync(runId, flowId, "manual", null, null);
        await store.MarkTimedOutAsync(runId, "Deadline exceeded");

        // Act
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        await _stepExecutor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default!);
        var status = await store.GetRunStatusAsync(runId);
        Assert.Equal("TimedOut", status);
    }

    [Fact]
    public async Task RunStepAsync_UserCancelledAndDeadlineLapsed_CompletesAsCancelled_NotTimedOut()
    {
        // Arrange — a run cancelled by the user whose deadline ALSO lapsed. Cancel intent must win:
        // the termination gate resolves Cancelled (not TimedOut), and the timeout latch is never set —
        // otherwise a later retry would clear the cancellation.
        var store = new InMemoryFlowRunStore();
        var engine = CreateEngine(store);
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = MakeSingleStepFlow(flowId);

        await store.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);
        // Deadline already in the past AND a user cancellation on record.
        await store.ConfigureRunAsync(runId, flowId, "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RequestCancelAsync(runId, "user requested");

        // Act
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("step1", "Work") { RunId = runId });

        // Assert
        await _stepExecutor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default!);
        Assert.Equal("Cancelled", await store.GetRunStatusAsync(runId));
        var control = await store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
    }
}
