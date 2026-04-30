using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Hosting;

/// <summary>
/// Concurrency tests for <see cref="FlowRunRecoveryHostedService"/> covering the
/// "Dispatch Many, Execute Once" invariant when multiple replicas race to recover
/// the same orphaned step. Uses a shared real <see cref="InMemoryFlowRunStore"/> to
/// model the atomic dispatch ledger across instances (Section G10 recovery races).
/// </summary>
public sealed class FlowRunRecoveryConcurrencyTests
{
    [Fact]
    public async Task StartAsync_TwoParallelRecoveryInstances_DispatcherCalledExactlyOnce()
    {
        // Arrange — single shared run store backs both recovery instances.
        var sharedStore = new InMemoryFlowRunStore();
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await sharedStore.StartRunAsync(flowId, "TestFlow", runId, "manual", null, null);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "DoWork" } }
        });

        var flowRepo = Substitute.For<IFlowRepository>();
        flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new IFlowDefinition[] { flow }));

        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>("job-1"));

        var outputsRepo = Substitute.For<IOutputsRepository>();
        var planner = new FlowGraphPlanner();

        FlowRunRecoveryHostedService MakeSut() =>
            new(
                sharedStore,
                new[] { (IFlowRunRuntimeStore)sharedStore },
                flowRepo,
                planner,
                dispatcher,
                outputsRepo,
                Substitute.For<ILogger<FlowRunRecoveryHostedService>>());

        var sut1 = MakeSut();
        var sut2 = MakeSut();

        // Act — race two recovery instances against the same store.
        await Task.WhenAll(sut1.StartAsync(default), sut2.StartAsync(default));

        // Assert — only one wins TryRecordDispatchAsync; only one EnqueueStepAsync call lands.
        await dispatcher.Received(1).EnqueueStepAsync(
            Arg.Any<IExecutionContext>(),
            flow,
            Arg.Is<IStepInstance>(s => s.Key == "step1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_RunReferencingUnknownFlow_DoesNotCallDispatcher()
    {
        // Arrange — active run references a flow ID that has been removed from the registry.
        // Recovery must skip without dispatching anything (logs a warning instead).
        var sharedStore = new InMemoryFlowRunStore();
        var orphanFlowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await sharedStore.StartRunAsync(orphanFlowId, "DeletedFlow", runId, "manual", null, null);

        var flowRepo = Substitute.For<IFlowRepository>();
        flowRepo.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(Array.Empty<IFlowDefinition>()));

        var dispatcher = Substitute.For<IStepDispatcher>();
        var outputsRepo = Substitute.For<IOutputsRepository>();
        var planner = new FlowGraphPlanner();

        var sut = new FlowRunRecoveryHostedService(
            sharedStore,
            new[] { (IFlowRunRuntimeStore)sharedStore },
            flowRepo,
            planner,
            dispatcher,
            outputsRepo,
            Substitute.For<ILogger<FlowRunRecoveryHostedService>>());

        // Act
        await sut.StartAsync(default);

        // Assert
        Assert.Empty(dispatcher.ReceivedCalls());
    }
}
