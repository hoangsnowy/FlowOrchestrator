using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Hosting;

/// <summary>
/// Recovery-time coverage for the ForEach completion barrier: a loop step that is still genuinely
/// parked when the host restarts must neither be settled early nor let the run be closed as a
/// zombie, and the step gated on it must stay undispatched.
/// </summary>
/// <remarks>
/// The complementary happy path — the host died after the last iteration finished but before the
/// continuation settled the loop — lives in <c>FlowRunRecoveryHostedServiceTests</c>. These tests
/// cover the other side of that boundary, which is where a recovery bug would silently reintroduce
/// the out-of-order execution issue #169 reported.
/// </remarks>
public sealed class FlowRunRecoveryLoopBarrierTests
{
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IFlowRunRuntimeStore _runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly ILogger<FlowRunRecoveryHostedService> _logger =
        Substitute.For<ILogger<FlowRunRecoveryHostedService>>();

    private FlowRunRecoveryHostedService CreateSut() =>
        new(_runStore, [_runtimeStore], _flowRepo, _graphPlanner, _dispatcher, _outputsRepo, _logger);

    private static IFlowDefinition LoopFlow(Guid flowId)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["loop"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    Steps = new StepCollection { ["child"] = new StepMetadata { Type = "DoWork" } }
                },
                ["after"] = new StepMetadata
                {
                    Type = "DoWork",
                    RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    private void ArrangeRun(Guid flowId, Guid runId, IFlowDefinition flow, Dictionary<string, StepStatus> statuses, int iterations)
    {
        _runStore.GetActiveRunsAsync().Returns(Task.FromResult<IReadOnlyList<FlowRunRecord>>(
            [new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" }]));
        _flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(statuses));
        _outputsRepo.GetStepOutputAsync(runId, "loop").Returns(ValueTask.FromResult<object?>(
            JsonSerializer.SerializeToElement(new { iterations }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal)));
        _runStore.TryRecordDispatchAsync(runId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
    }

    [Fact]
    public async Task StartAsync_LoopStillHasAnUnfinishedIteration_DoesNotSettleOrDispatchDownstream()
    {
        // Arrange - iteration 0 finished, iteration 1 was left Running by the worker that died.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = LoopFlow(flowId);
        ArrangeRun(flowId, runId, flow, new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Running,
            ["loop.0.child"] = StepStatus.Succeeded,
            ["loop.1.child"] = StepStatus.Running
        }, iterations: 2);

        // Act
        await CreateSut().StartAsync(default);

        // Assert - settling here would let "after" run while an iteration is still outstanding,
        // which is exactly the ordering bug the barrier exists to prevent.
        await _runStore.DidNotReceiveWithAnyArgs().RecordStepCompleteAsync(default, default!, default!, default, default);
        await _dispatcher.DidNotReceiveWithAnyArgs().EnqueueStepAsync(default!, default!, default!, default);
        await _runStore.DidNotReceiveWithAnyArgs().CompleteRunAsync(default, default!);
    }

    [Fact]
    public async Task StartAsync_LoopFannedOutButNoIterationEverStarted_DoesNotSettleAndDoesNotCloseTheRun()
    {
        // Arrange - the host died between the loop's fan-out dispatch and the first iteration
        // being picked up, so only the loop step has a status row.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = LoopFlow(flowId);
        ArrangeRun(flowId, runId, flow, new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Running
        }, iterations: 3);

        // Act
        await CreateSut().StartAsync(default);

        // Assert - the loop must stay parked (no iteration is terminal) and the run must not be
        // classified as a zombie just because its iterations have no rows yet.
        await _runStore.DidNotReceiveWithAnyArgs().RecordStepCompleteAsync(default, default!, default!, default, default);
        await _runStore.DidNotReceiveWithAnyArgs().CompleteRunAsync(default, default!);
        await _dispatcher.DidNotReceiveWithAnyArgs().EnqueueStepAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task StartAsync_LoopWithAllIterationsSkipped_SettlesAndDispatchesDownstream()
    {
        // Arrange - every iteration was cascade- or When-skipped before the crash. Skipped is
        // terminal, so the barrier must settle rather than deadlock the run.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = LoopFlow(flowId);
        var parked = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Running,
            ["loop.0.child"] = StepStatus.Skipped,
            ["loop.1.child"] = StepStatus.Skipped
        };
        var settled = new Dictionary<string, StepStatus>(parked) { ["loop"] = StepStatus.Succeeded };

        _runStore.GetActiveRunsAsync().Returns(Task.FromResult<IReadOnlyList<FlowRunRecord>>(
            [new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" }]));
        _flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));
        _runtimeStore.GetStepStatusesAsync(runId).Returns(
            _ => Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(parked),
            _ => Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(settled));
        _outputsRepo.GetStepOutputAsync(runId, "loop").Returns(ValueTask.FromResult<object?>(
            JsonSerializer.SerializeToElement(new { iterations = 2 }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal)));
        _runStore.TryRecordDispatchAsync(runId, "after", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        await CreateSut().StartAsync(default);

        // Assert
        await _runStore.Received(1).RecordStepCompleteAsync(
            runId, "loop", nameof(StepStatus.Succeeded), """{"iterations":2}""", null);
        await _dispatcher.Received(1).EnqueueStepAsync(
            Arg.Is<IExecutionContext>(c => c!.RunId == runId),
            flow,
            Arg.Is<IStepInstance>(s => s!.Key == "after"),
            Arg.Any<CancellationToken>());
    }
}
