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
}
