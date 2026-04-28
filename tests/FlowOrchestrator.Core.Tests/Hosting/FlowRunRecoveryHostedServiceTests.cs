using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using FluentAssertions;
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

    // ── helpers ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<FlowRunRecord> RunList(params FlowRunRecord[] runs) => runs;
    private static IReadOnlyDictionary<string, StepStatus> EmptyStatuses() =>
        new Dictionary<string, StepStatus>();
    private static IReadOnlySet<string> EmptyDispatched() =>
        new HashSet<string>(StringComparer.Ordinal);
    private static IReadOnlySet<string> DispatchedSet(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.Ordinal);

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_NoActiveRuns_DoesNotDispatch()
    {
        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult<IReadOnlyList<FlowRunRecord>>(Array.Empty<FlowRunRecord>()));

        await CreateSut().StartAsync(default);

        _dispatcher.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_NoRuntimeStore_SkipsRecovery()
    {
        await CreateSut(includeRuntimeStore: false).StartAsync(default);

        // GetActiveRunsAsync must NOT have been called — we bail out early.
        await _runStore.DidNotReceiveWithAnyArgs().GetActiveRunsAsync();
        _dispatcher.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_ReadyStepNotDispatched_EnqueuesStep()
    {
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = FlowWith(flowId, "step1");

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new IFlowDefinition[] { flow }));

        // step1 has no dependencies → graph evaluates it as Ready.
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult(EmptyStatuses()));

        // No dispatch row yet — recovery should enqueue it.
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(EmptyDispatched()));

        _runStore.TryRecordDispatchAsync(runId, "step1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await CreateSut().StartAsync(default);

        await _dispatcher.Received(1).EnqueueStepAsync(
            Arg.Is<IExecutionContext>(c => c.RunId == runId),
            flow,
            Arg.Is<IStepInstance>(s => s.Key == "step1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ReadyStepAlreadyDispatched_DoesNotReEnqueue()
    {
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = FlowWith(flowId, "step1");

        _runStore.GetActiveRunsAsync()
            .Returns(Task.FromResult(RunList(new FlowRunRecord { Id = runId, FlowId = flowId, Status = "Running" })));
        _flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new IFlowDefinition[] { flow }));
        _runtimeStore.GetStepStatusesAsync(runId)
            .Returns(Task.FromResult(EmptyStatuses()));

        // Dispatch row already exists → recovery must skip it.
        _runStore.GetDispatchedStepKeysAsync(runId)
            .Returns(Task.FromResult(DispatchedSet("step1")));

        await CreateSut().StartAsync(default);

        _dispatcher.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_TryRecordDispatchReturnsFalse_DoesNotCallDispatcher()
    {
        // Two recovery replicas race — only one wins TryRecordDispatch.
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

        // This instance lost the atomic race.
        _runStore.TryRecordDispatchAsync(runId, "step1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        await CreateSut().StartAsync(default);

        _dispatcher.ReceivedCalls().Should().BeEmpty();
    }
}
