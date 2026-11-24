using System.Threading.Channels;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Restart recovery for the in-memory RUNTIME paired with a DURABLE store — the combination
/// <c>docs/articles/configuration.md</c> documents as supported ("<c>UseInMemoryRuntime()</c> works equally
/// with <c>UseSqlServer()</c> or <c>UsePostgreSql()</c> … durable run state").
/// </summary>
/// <remarks>
/// <para>
/// Every step the engine hands to <see cref="InMemoryStepDispatcher"/> first gets a dispatch-ledger row in
/// the durable store; the step itself then lives only in the process's <see cref="Channel{T}"/> (or in a
/// pending <c>Task.Delay</c> for a scheduled poll). When the process stops, the channel and the delays are
/// gone but the ledger rows are not. On the next start <see cref="FlowRunRecoveryHostedService"/> treats a
/// ledger row as "already in the runtime queue; the worker will pick it up" and skips the step — which is
/// true for Hangfire storage and the Service Bus broker, and false for the in-memory channel. The step is
/// never run again and the run stays Running for ever.
/// </para>
/// <para>
/// <c>InMemoryStepDispatcher.DeliverAfterDelayAsync</c> even documents the opposite ("recovery hosted
/// service will re-enqueue on next startup"). The durable store is played here by
/// <see cref="InMemoryFlowRunStore"/>, reused across the simulated restart.
/// </para>
/// </remarks>
public sealed class InMemoryRuntimeDurableStoreRecoveryTests
{
    [Fact]
    public async Task Recovery_AfterRestart_RedispatchesAStepThatWasOnlyInTheLostChannel()
    {
        // Arrange — process 1: the engine reserved the entry step in the ledger and wrote it to its channel,
        // then the process stopped before the runner drained it (exactly what TriggerAsync does).
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "DoWork" } }
        });

        var durableStore = new InMemoryFlowRunStore();
        var outputs = new InMemoryOutputsRepository(durableStore);
        await durableStore.StartRunAsync(flowId, "Flow", runId, "manual", null, null);

        var processOneChannel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var processOneDispatcher = new InMemoryStepDispatcher(processOneChannel.Writer);
        Assert.True(await durableStore.TryRecordDispatchAsync(runId, "step1"));
        await processOneDispatcher.EnqueueStepAsync(
            new Core.Execution.ExecutionContext { RunId = runId }, flow, new StepInstance("step1", "DoWork") { RunId = runId });
        processOneChannel.Writer.Complete(); // process 1 is gone; its channel content with it

        // Process 2 starts with a fresh channel over the same durable store.
        var processTwoChannel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var recovery = new FlowRunRecoveryHostedService(
            durableStore,
            [durableStore],
            new FlowRegistry([flow]),
            new FlowGraphPlanner(),
            new InMemoryStepDispatcher(processTwoChannel.Writer),
            outputs,
            NullLogger<FlowRunRecoveryHostedService>.Instance);

        // Act
        await recovery.StartAsync(CancellationToken.None);

        // Assert — the step must reach the new process's runner; nothing else will ever run it.
        Assert.True(processTwoChannel.Reader.TryRead(out var recovered),
            "Recovery skipped 'step1' because its dispatch-ledger row survived the restart, but the in-memory channel that held the step did not.");
        Assert.Equal("step1", recovered!.Step.Key);
    }

    private static IFlowDefinition SingleStepFlow(Guid flowId, string stepKey = "step1")
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { [stepKey] = new StepMetadata { Type = "DoWork" } }
        });
        return flow;
    }

    private static FlowRunRecoveryHostedService CreateRecovery(
        InMemoryFlowRunStore store, IFlowDefinition flow, IStepDispatcher dispatcher) =>
        new(store, [store], new FlowRegistry([flow]), new FlowGraphPlanner(), dispatcher,
            new InMemoryOutputsRepository(store), NullLogger<FlowRunRecoveryHostedService>.Instance);

    [Fact]
    public async Task Recovery_AfterRestart_RedispatchesAPendingPollWhoseDelayedAttemptWasLost()
    {
        // Arrange — the poll returned Pending: the engine released the claim, re-reserved the ledger row and
        // handed the next attempt to a Task.Delay that died with process 1.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = SingleStepFlow(flowId, "poll");
        var store = new InMemoryFlowRunStore();
        await store.StartRunAsync(flowId, "Flow", runId, "manual", null, null);
        Assert.True(await store.TryRecordDispatchAsync(runId, "poll"));
        Assert.True(await store.TryClaimStepAsync(runId, "poll"));
        await store.RecordStepStartAsync(runId, "poll", "DoWork", null, null);
        await store.RecordStepCompleteAsync(runId, "poll", nameof(StepStatus.Pending), null, null);
        await store.ReleaseStepClaimAsync(runId, "poll");

        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var recovery = CreateRecovery(store, flow, new InMemoryStepDispatcher(channel.Writer));

        // Act
        await recovery.StartAsync(CancellationToken.None);

        // Assert — the next poll attempt is queued again; no second ledger row was needed.
        Assert.True(channel.Reader.TryRead(out var recovered), "The Pending poll was not re-enqueued after the restart.");
        Assert.Equal("poll", recovered!.Step.Key);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(true)]   // claimed and still without a status row: its claimer may still be running it
    [InlineData(false)]  // already finished: nothing was lost
    public async Task Recovery_AfterRestart_LeavesAClaimedOrFinishedLedgeredStepAlone(bool claimedWithoutStatus)
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = SingleStepFlow(flowId);
        var store = new InMemoryFlowRunStore();
        await store.StartRunAsync(flowId, "Flow", runId, "manual", null, null);
        Assert.True(await store.TryRecordDispatchAsync(runId, "step1"));
        Assert.True(await store.TryClaimStepAsync(runId, "step1"));
        if (!claimedWithoutStatus)
        {
            await store.RecordStepStartAsync(runId, "step1", "DoWork", null, null);
            await store.RecordStepCompleteAsync(runId, "step1", nameof(StepStatus.Succeeded), null, null);
        }

        var channel = Channel.CreateUnbounded<InMemoryStepEnvelope>();
        var recovery = CreateRecovery(store, flow, new InMemoryStepDispatcher(channel.Writer));

        // Act
        await recovery.StartAsync(CancellationToken.None);

        // Assert
        Assert.False(channel.Reader.TryRead(out var unexpected), $"Recovery re-enqueued '{unexpected?.Step.Key}'.");
    }

    [Fact]
    public async Task Recovery_DurableRuntime_TrustsTheLedgerAndDoesNotRedispatch()
    {
        // Arrange — same lost-channel state, but the dispatcher reports a durable queue (the default).
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = SingleStepFlow(flowId);
        var store = new InMemoryFlowRunStore();
        await store.StartRunAsync(flowId, "Flow", runId, "manual", null, null);
        Assert.True(await store.TryRecordDispatchAsync(runId, "step1"));
        var durable = Substitute.For<IStepDispatcher>();
        durable.LosesQueuedStepsOnRestart.Returns(false);
        var recovery = CreateRecovery(store, flow, durable);

        // Act
        await recovery.StartAsync(CancellationToken.None);

        // Assert — the runtime still holds the step; recovery must not hand it over a second time.
        await durable.DidNotReceiveWithAnyArgs().EnqueueStepAsync(default!, default!, default!, default);
    }

    [Fact]
    public void InMemoryStepDispatcher_ReportsThatQueuedStepsAreLostOnRestart()
    {
        // Arrange
        var sut = new InMemoryStepDispatcher(Channel.CreateUnbounded<InMemoryStepEnvelope>().Writer);

        // Act
        var loses = ((IStepDispatcher)sut).LosesQueuedStepsOnRestart;

        // Assert
        Assert.True(loses);
    }
}
