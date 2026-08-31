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
    private readonly IFlowRepository _flowRepo = Substitute.For<IFlowRepository>();
    private readonly IFlowGraphPlanner _graphPlanner = new FlowGraphPlanner();
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IOutputsRepository _outputsRepo = Substitute.For<IOutputsRepository>();
    private readonly ILogger<FlowRunRecoveryHostedService> _logger = Substitute.For<ILogger<FlowRunRecoveryHostedService>>();

    private FlowRunRecoveryHostedService CreateSut(bool includeRuntimeStore = true) =>
        new(
            _runStore,
            includeRuntimeStore ? new[] { _runtimeStore } : Array.Empty<IFlowRunRuntimeStore>(),
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
    public async Task StartAsync_NoRuntimeStore_SkipsRecovery()
    {
        // Arrange

        // Act
        await CreateSut(includeRuntimeStore: false).StartAsync(default);

        // Assert
        await _runStore.DidNotReceiveWithAnyArgs().GetActiveRunsAsync();
        Assert.Empty(_dispatcher.ReceivedCalls());
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
        _flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new IFlowDefinition[] { flow }));
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
    public async Task StartAsync_ReadyStepAlreadyDispatched_DoesNotReEnqueue()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = FlowWith(flowId, "step1");

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new IFlowDefinition[] { flow }));
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult(EmptyStatuses()));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(DispatchedSet("step1")));

        // Act
        await CreateSut().StartAsync(default);

        // Assert
        Assert.Empty(_dispatcher.ReceivedCalls());
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
        _flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new IFlowDefinition[] { flow }));
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
        _flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new IFlowDefinition[] { flow }));
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
        _flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new IFlowDefinition[] { flow }));
        // First read sees the parked loop; the read after settling sees it Succeeded.
        _runtimeStore.GetStepStatusesAsync(runId).Returns(
            _ => Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(parked),
            _ => Task.FromResult<IReadOnlyDictionary<string, StepStatus>>(settledView));
        _outputsRepo.GetStepOutputAsync(runId, "loop")
            .Returns(ValueTask.FromResult<object?>(
                JsonSerializer.SerializeToElement(new { iterations = 2 }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(EmptyDispatched()));
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
