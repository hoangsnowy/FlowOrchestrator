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
/// Per-run fault isolation inside <see cref="FlowOrchestratorEngine.EnforceDueTimeoutsAsync"/>: an
/// <see cref="OperationCanceledException"/> that the sweep did not request (an HTTP-backed store's client
/// timeout) is one run's fault and must not abort the sweep for every later run; one raised while the
/// sweep's own token is cancelled is shutdown and must propagate.
/// </summary>
public sealed class TimeoutSweepRunIsolationTests
{
    private static (FlowOrchestratorEngine Engine, InMemoryFlowRunStore Store) Build(IFlowDefinition flow, Func<InMemoryFlowRunStore, IFlowRunControlStore> control)
    {
        var store = new InMemoryFlowRunStore();
        var engine = new FlowOrchestratorEngine(
            Substitute.For<IStepDispatcher>(),
            new FlowGraphPlanner(),
            Substitute.For<IStepExecutor>(),
            Substitute.For<IFlowStore>(),
            store,
            new InMemoryOutputsRepository(store),
            Substitute.For<IExecutionContextAccessor>(),
            new FlowRegistry([flow]),
            [store],
            [control(store)],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            Substitute.For<ILogger<FlowOrchestratorEngine>>(),
            NoopFlowEventNotifier.Instance);
        return (engine, store);
    }

    private static IFlowDefinition Flow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = new StepCollection { ["a"] = new StepMetadata { Type = "Echo" } } });
        return flow;
    }

    private static async Task<Guid> StartCancelledRunAsync(InMemoryFlowRunStore store, IFlowDefinition flow)
    {
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flow.Id, "SweepFlow", runId, "manual", null, null);
        await store.ConfigureRunAsync(runId, flow.Id, "manual", null, null);
        await store.RequestCancelAsync(runId, "user");
        return runId;
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_StrayOperationCanceledOnOneRun_StillClosesTheOtherRun()
    {
        // Arrange — the control read of whichever run the sweep visits first times out client-side.
        var flow = Flow();
        FaultingControlStore? faulting = null;
        var (engine, store) = Build(flow, s => faulting = new FaultingControlStore(s, _ => new TaskCanceledException("store client timed out")));
        var first = await StartCancelledRunAsync(store, flow);
        var second = await StartCancelledRunAsync(store, flow);

        // Act
        var ex = await Record.ExceptionAsync(() => engine.EnforceDueTimeoutsAsync(CancellationToken.None));

        // Assert — the faulted run stays Running for the next tick; the other one is closed.
        Assert.Null(ex);
        var statuses = new[] { await store.GetRunStatusAsync(first), await store.GetRunStatusAsync(second) };
        Assert.Contains("Running", statuses);
        Assert.Contains("Cancelled", statuses);
        Assert.NotNull(faulting!.FaultedRunId);
    }

    [Fact]
    public async Task EnforceDueTimeoutsAsync_OperationCanceledWhileSweepTokenIsCancelled_Propagates()
    {
        // Arrange — shutdown lands while the first run's control read is in progress.
        var flow = Flow();
        using var shutdown = new CancellationTokenSource();
        var (engine, store) = Build(flow, s => new FaultingControlStore(s, _ =>
        {
            shutdown.Cancel();
            return new OperationCanceledException(shutdown.Token);
        }));
        await StartCancelledRunAsync(store, flow);

        // Act
        var ex = await Record.ExceptionAsync(() => engine.EnforceDueTimeoutsAsync(shutdown.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>
    /// Control-store decorator whose <see cref="GetRunControlAsync"/> throws for the first run it is asked
    /// about and delegates every other call to the in-memory store.
    /// </summary>
    private sealed class FaultingControlStore(InMemoryFlowRunStore inner, Func<Guid, Exception> fault) : IFlowRunControlStore
    {
        public Guid? FaultedRunId { get; private set; }

        public Task<FlowRunControlRecord?> GetRunControlAsync(Guid runId)
        {
            if (FaultedRunId is null || FaultedRunId == runId)
            {
                FaultedRunId = runId;
                return Task.FromException<FlowRunControlRecord?>(fault(runId));
            }

            return inner.GetRunControlAsync(runId);
        }

        public Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc)
            => inner.ConfigureRunAsync(runId, flowId, triggerKey, idempotencyKey, timeoutAtUtc);

        public Task<bool> RequestCancelAsync(Guid runId, string? reason) => inner.RequestCancelAsync(runId, reason);

        public Task<bool> MarkTimedOutAsync(Guid runId, string? reason) => inner.MarkTimedOutAsync(runId, reason);

        public Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
            => inner.FindRunIdByIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey);

        public Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
            => inner.TryRegisterIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey, runId);

        public Task<Guid?> TryTakeOverStaleIdempotencyKeyAsync(
            Guid flowId, string triggerKey, string idempotencyKey, Guid staleRunId, Guid newRunId, TimeSpan reservationTimeout)
            => inner.TryTakeOverStaleIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey, staleRunId, newRunId, reservationTimeout);
    }
}
