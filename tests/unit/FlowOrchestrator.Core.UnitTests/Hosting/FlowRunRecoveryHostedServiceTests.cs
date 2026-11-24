using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Hosting;

public class FlowRunRecoveryHostedServiceTests
{
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IFlowRunRuntimeStore _runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
    private FlowRegistry _flowRepo = FlowRegistry.Empty;
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly ILogger<FlowRunRecoveryHostedService> _logger = Substitute.For<ILogger<FlowRunRecoveryHostedService>>();

    private FlowRunRecoveryHostedService CreateSut() =>
        new(
            _runStore,
            new[] { _runtimeStore },
            _flowRepo,
            _graphPlanner,
            _dispatcher,
            _outputsRepo,
            _logger);

    private static IFlowDefinition FlowWith(Guid flowId, string stepKey)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { [stepKey] = new StepMetadata { Type = "DoWork" } }
        });
        return flow;
    }

    private static IReadOnlyList<FlowRunRecord> RunList(params FlowRunRecord[] runs) => runs;
    private static IReadOnlyDictionary<string, StepStatus> EmptyStatuses() =>
        new Dictionary<string, StepStatus>();
    private static IReadOnlySet<string> EmptyDispatched() =>
        new HashSet<string>(StringComparer.Ordinal);
    private static IReadOnlySet<string> DispatchedSet(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.Ordinal);

    [Fact]
    public async Task StartAsync_NoActiveRuns_DoesNotDispatch()
    {
        // Arrange
        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult<IReadOnlyList<FlowRunRecord>>(Array.Empty<FlowRunRecord>()));

        // Act
        await CreateSut().StartAsync(default);

        // Assert
        Assert.Empty(_dispatcher.ReceivedCalls());
    }

    [Fact]
    public void Constructor_NoRuntimeStore_FailsFastNamingServiceAndStore()
    {
        // Arrange — v2.0 removed the "skip recovery in legacy sequential mode" fallback: a host
        // without a runtime store cannot evaluate step statuses, so it must not start silently.
        var noStores = Array.Empty<IFlowRunRuntimeStore>();

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => new FlowRunRecoveryHostedService(
            _runStore,
            noStores,
            _flowRepo,
            _graphPlanner,
            _dispatcher,
            _outputsRepo,
            _logger));

        // Assert
        Assert.Contains(nameof(FlowRunRecoveryHostedService), ex.Message);
        Assert.Contains(nameof(IFlowRunRuntimeStore), ex.Message);
    }

    [Fact]
    public async Task StartAsync_ReadyStepNotDispatched_EnqueuesStep()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = FlowWith(flowId, "step1");

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo = new FlowRegistry(new IFlowDefinition[] { flow });
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult(EmptyStatuses()));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(EmptyDispatched()));
        _runStore.TryRecordDispatchAsync(runId, "step1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        await CreateSut().StartAsync(default);

        // Assert
        await _dispatcher.Received(1).EnqueueStepAsync(
            Arg.Is<IExecutionContext>(c => c!.RunId == runId),
            flow,
            Arg.Is<IStepInstance>(s => s!.Key == "step1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ShutdownAfterTheLedgerRowWasRecorded_StillDispatchesWithAnUncancellableToken()
    {
        // Arrange - host shutdown cancels the StartAsync token right after recovery recorded the ledger row
        // (RFC 2.0 P-1(d)). Handing that token to the dispatcher aborted the hand-off with the row in place.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = FlowWith(flowId, "step1");
        using var shutdown = new CancellationTokenSource();

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo = new FlowRegistry(new IFlowDefinition[] { flow });
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult(EmptyStatuses()));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(EmptyDispatched()));
        _runStore.TryRecordDispatchAsync(runId, "step1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                shutdown.Cancel();
                return Task.FromResult(true);
            });
        _dispatcher.EnqueueStepAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(new ValueTask<string?>("job-1"));

        // Act
        await CreateSut().StartAsync(shutdown.Token);

        // Assert
        await _dispatcher.Received(1).EnqueueStepAsync(
            Arg.Is<IExecutionContext>(c => c!.RunId == runId),
            flow,
            Arg.Is<IStepInstance>(s => s!.Key == "step1"),
            Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
        await _runStore.Received(1).AnnotateDispatchAsync(
            runId, "step1", "job-1", Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
        await _runStore.DidNotReceiveWithAnyArgs().ReleaseDispatchAsync(default, default!, default);
    }

    [Fact]
    public async Task StartAsync_ReadyStepAlreadyDispatched_DoesNotReEnqueue()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = FlowWith(flowId, "step1");

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo = new FlowRegistry(new IFlowDefinition[] { flow });
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult(EmptyStatuses()));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(DispatchedSet("step1")));

        // Act
        await CreateSut().StartAsync(default);

        // Assert — the substitute reports the default (durable runtime): a ledger row means the runtime
        // still holds the step. Reading that capability is the only call the dispatcher may see.
        await _dispatcher.DidNotReceiveWithAnyArgs().EnqueueStepAsync(default!, default!, default!, default);
        await _dispatcher.DidNotReceiveWithAnyArgs().ScheduleStepAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task StartAsync_TryRecordDispatchReturnsFalse_DoesNotCallDispatcher()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = FlowWith(flowId, "step1");

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo = new FlowRegistry(new IFlowDefinition[] { flow });
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult(EmptyStatuses()));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(EmptyDispatched()));
        _runStore.TryRecordDispatchAsync(runId, "step1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Act
        await CreateSut().StartAsync(default);

        // Assert
        Assert.Empty(_dispatcher.ReceivedCalls());
    }

    [Fact]
    public async Task StartAsync_DispatcherThrowsAfterLedgerRecorded_ReleasesTheLedgerRow()
    {
        // Arrange — the dispatch-ledger row is recorded, then the runtime call fails (broker blip).
        // Left in place the row says "an attempt is queued" for one the runtime never received, and
        // recovery skips ledgered keys, so the step would never run again.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = FlowWith(flowId, "step1");

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo = new FlowRegistry(new IFlowDefinition[] { flow });
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult(EmptyStatuses()));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(EmptyDispatched()));
        _runStore.TryRecordDispatchAsync(runId, "step1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _dispatcher.EnqueueStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => throw new InvalidOperationException("broker down"));

        // Act — the per-run isolation in recovery keeps startup alive.
        var ex = await Record.ExceptionAsync(() => CreateSut().StartAsync(default));

        // Assert
        Assert.Null(ex);
        await _runStore.Received(1).ReleaseDispatchAsync(runId, "step1", CancellationToken.None);
        await _runStore.DidNotReceive().AnnotateDispatchAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_StepWithUnsatisfiedDependency_IsNotDispatched()
    {
        // Arrange — step1 is still Running (its worker died mid-execution); step2 depends on it.
        // Recovery must not run step2 out of order, which is the issue #169 failure mode.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "DoWork" },
                ["step2"] = new StepMetadata
                {
                    Type = "DoWork",
                    RunAfter = new RunAfterCollection { ["step1"] = [StepStatus.Succeeded] }
                }
            }
        });

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo = new FlowRegistry(new IFlowDefinition[] { flow });
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(
                new Dictionary<string, StepStatus> { ["step1"] = StepStatus.Running }));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(EmptyDispatched()));

        // Act
        await CreateSut().StartAsync(default);

        // Assert
        Assert.Empty(_dispatcher.ReceivedCalls());
        await _runStore.DidNotReceiveWithAnyArgs().CompleteRunAsync(default, default!);
    }

    [Fact]
    public async Task StartAsync_LoopBarrierCompletedWhileHostWasDown_SettlesLoopAndDispatchesDownstream()
    {
        // Arrange — the host crashed after the last iteration finished but before the
        // continuation settled the loop step, leaving it parked on its barrier forever.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
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

        var parked = new Dictionary<string, StepStatus>
        {
            ["loop"] = StepStatus.Running,
            ["loop.0.child"] = StepStatus.Succeeded,
            ["loop.1.child"] = StepStatus.Succeeded
        };
        var settledView = new Dictionary<string, StepStatus>(parked) { ["loop"] = StepStatus.Succeeded };

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo = new FlowRegistry(new IFlowDefinition[] { flow });
        // First read sees the parked loop; the read after settling sees it Succeeded.
        _runtimeStore.GetStepStatusesAsync(runId).Returns(
            _ => Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(parked),
            _ => Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(settledView));
        _outputsRepo.GetStepOutputAsync(runId, "loop")
            .Returns(ValueTask.FromResult<object?>(
                JsonSerializer.SerializeToElement(new { iterations = 2 }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(EmptyDispatched()));
        _runStore.TryCompleteRunningStepAsync(runId, "loop", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.FromResult(true));
        _runStore.TryRecordDispatchAsync(runId, "after", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        await CreateSut().StartAsync(default);

        // Assert
        await _runStore.Received(1).TryCompleteRunningStepAsync(
            runId, "loop", nameof(StepStatus.Succeeded), """{"iterations":2}""", null);
        await _dispatcher.Received(1).EnqueueStepAsync(
            Arg.Is<IExecutionContext>(c => c!.RunId == runId),
            flow,
            Arg.Is<IStepInstance>(s => s!.Key == "after"),
            Arg.Any<CancellationToken>());
    }
}
