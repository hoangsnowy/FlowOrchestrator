using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Tests for run-level control features: idempotency key deduplication,
/// cancellation enforcement, and timeout enforcement.
/// Uses the real <see cref="InMemoryFlowRunStore"/> instead of mocks so the
/// full control-record lifecycle is exercised without SQL infrastructure.
/// </summary>
public sealed class RunControlTests
{
    // ── Substitutes ───────────────────────────────────────────────────────────

    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IFlowExecutor _flowExecutor = Substitute.For<IFlowExecutor>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepExecutor _stepExecutor = Substitute.For<IStepExecutor>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly IExecutionContextAccessor _ctxAccessor = Substitute.For<IExecutionContextAccessor>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly ILogger<FlowOrchestratorEngine> _logger =
        Substitute.For<ILogger<FlowOrchestratorEngine>>();

    // ── Factory helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates an engine backed by <paramref name="store"/> for all storage roles.
    /// </summary>
    private FlowOrchestratorEngine CreateEngine(InMemoryFlowRunStore store) =>
        new FlowOrchestratorEngine(
            _dispatcher,
            _flowExecutor,
            _graphPlanner,
            _stepExecutor,
            store,          // IFlowRunStore
            _outputsRepo,
            _ctxAccessor,
            _flowRepo,
            [store],        // IFlowRunRuntimeStore
            [store],        // IFlowRunControlStore
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
        // Arrange: two triggers share the same Idempotency-Key header.
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
                headers: new Dictionary<string, string>
                {
                    ["Idempotency-Key"] = idempotencyKey
                })
        };

        // Act: first trigger creates the run.
        await engine.TriggerAsync(firstCtx);
        var firstRunId = firstCtx.RunId;
        firstRunId.Should().NotBe(Guid.Empty);

        // Act: second trigger with the same idempotency key.
        var secondCtx = new TriggerContext
        {
            RunId = Guid.NewGuid(),   // different initial RunId — engine should override it
            Flow = flow,
            Trigger = new Trigger(
                "manual", "Manual", null,
                headers: new Dictionary<string, string>
                {
                    ["Idempotency-Key"] = idempotencyKey
                })
        };

        await engine.TriggerAsync(secondCtx);

        // Assert: the engine must have detected the duplicate and reused the first RunId.
        secondCtx.RunId.Should().Be(firstRunId,
            "duplicate triggers with the same idempotency key must be collapsed into the original run");

        // The store should still have exactly one run record for this flow.
        var runs = await store.GetRunsAsync(flowId: flowId);
        runs.Should().HaveCount(1, "no second run must have been started for a duplicate trigger");
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
        // Signal cancellation before RunStepAsync is called.
        await store.RequestCancelAsync(runId, "Test cancellation");

        // Act
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("step1", "Work") { RunId = runId });

        // Assert: the step executor must NOT have been invoked.
        await _stepExecutor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default!);

        // The run must be marked Cancelled.
        var status = await store.GetRunStatusAsync(runId);
        status.Should().Be("Cancelled",
            "a run with CancelRequested = true must be completed as Cancelled before any step handler is called");
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
        // Mark the run as timed out before RunStepAsync is called.
        await store.MarkTimedOutAsync(runId, "Deadline exceeded");

        // Act
        await engine.RunStepAsync(
            new CoreExecutionContext { RunId = runId },
            flow,
            new StepInstance("step1", "Work") { RunId = runId });

        // Assert: executor must not have been called.
        await _stepExecutor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default!);

        // The run must be marked TimedOut.
        var status = await store.GetRunStatusAsync(runId);
        status.Should().Be("TimedOut",
            "a run whose timeout deadline has passed must be completed as TimedOut without executing any steps");
    }
}
