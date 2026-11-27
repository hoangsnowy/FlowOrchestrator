using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// RFC 2.0 S1b / P-1(b): a step that is claimed but has no status row. A failed start write must not
/// leave that state behind, and a claim orphaned by a crash between claim and start must stop holding the
/// run open once it is older than <see cref="FlowRunControlOptions.OrphanedClaimTimeout"/>.
/// </summary>
public sealed class ClaimWithoutStatusRowTests
{
    private static IFlowDefinition MakeSingleStepFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "Work" } }
        });
        return flow;
    }

    private static IStepResult Succeeded(string stepKey) =>
        new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };

    [Fact]
    public async Task FailedStartWrite_releasesTheClaimAndTheLedgerRow_rethrows_andTheRuntimeRetryRunsTheStep()
    {
        // Arrange - the start write of step1 fails once, as a store blip would.
        var executions = 0;
        FaultInjectingRunStore? faults = null;
        var harness = new LoopBarrierEngineHarness(
            MakeSingleStepFlow(),
            stepKey =>
            {
                Interlocked.Increment(ref executions);
                return Succeeded(stepKey);
            },
            runStore: store => faults = new FaultInjectingRunStore(store));

        var runId = await harness.TriggerAsync();
        faults!.FailNextStepStart("step1", new InvalidOperationException("store blip"));

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunKeyAsync("step1"));
        var claimedAfterFailure = await harness.Store.IsStepClaimedAsync(runId, "step1");
        var ledgeredAfterFailure = await harness.Store.IsStepDispatchedAsync(runId, "step1");
        var executionsAfterFailure = Volatile.Read(ref executions);
        await harness.RedeliverAsync(runId, "step1", "Work");

        // Assert - the failure surfaced to the runtime, the handler did not run without a status row,
        // nothing was left holding the step, and the runtime's retry of the job ran it once.
        Assert.Equal("store blip", thrown.Message);
        Assert.False(claimedAfterFailure);
        Assert.False(ledgeredAfterFailure);
        Assert.Equal(0, executionsAfterFailure);
        Assert.Equal(1, Volatile.Read(ref executions));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "step1"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task TimeoutSweep_onACancelledRunHeldOpenByAnOrphanedClaim_closesTheRun()
    {
        // Arrange - step1 was claimed by a worker that died before its start write: claim, ledger row,
        // no status row. The run is then cancelled.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new LoopBarrierEngineHarness(
            MakeSingleStepFlow(),
            Succeeded,
            new FlowRunControlOptions { OrphanedClaimTimeout = TimeSpan.FromSeconds(60) });
        harness.Store.Clock = clock;
        var runId = await harness.TriggerAsync();
        Assert.True(await harness.Store.TryClaimStepAsync(runId, "step1"));
        await harness.Store.RequestCancelAsync(runId, "operator");
        clock.Advance(TimeSpan.FromSeconds(61));

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
        Assert.False(await harness.Store.IsStepClaimedAsync(runId, "step1"));
    }

    [Fact]
    public async Task TimeoutSweep_leavesAClaimYoungerThanTheTimeoutAlone()
    {
        // Arrange - same shape, but the claim is only 10 s old: its worker may still be starting the step.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new LoopBarrierEngineHarness(
            MakeSingleStepFlow(),
            Succeeded,
            new FlowRunControlOptions { OrphanedClaimTimeout = TimeSpan.FromSeconds(60) });
        harness.Store.Clock = clock;
        var runId = await harness.TriggerAsync();
        Assert.True(await harness.Store.TryClaimStepAsync(runId, "step1"));
        await harness.Store.RequestCancelAsync(runId, "operator");
        clock.Advance(TimeSpan.FromSeconds(10));

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        Assert.Equal("Running", await harness.RunStatusAsync(runId));
        Assert.True(await harness.Store.IsStepClaimedAsync(runId, "step1"));
    }

    [Fact]
    public async Task StartupRecovery_releasesAnOrphanedClaim_andReDispatchesTheStep()
    {
        // Arrange - a crash between claim and start write left step1 claimed and ledgered with no row.
        var (store, dispatcher, flow, runId, clock) = await SeedOrphanedClaimAsync();
        clock.Advance(TimeSpan.FromSeconds(61));
        var sut = CreateRecovery(store, dispatcher, flow);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        await dispatcher.Received(1).EnqueueStepAsync(
            Arg.Is<IExecutionContext>(c => c.RunId == runId),
            flow,
            Arg.Is<IStepInstance>(s => s.Key == "step1"),
            Arg.Any<CancellationToken>());
        Assert.False(await store.IsStepClaimedAsync(runId, "step1"));
        Assert.True(await store.IsStepDispatchedAsync(runId, "step1"));
    }

    [Fact]
    public async Task StartupRecovery_leavesAYoungClaimAlone()
    {
        // Arrange
        var (store, dispatcher, flow, runId, clock) = await SeedOrphanedClaimAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        var sut = CreateRecovery(store, dispatcher, flow);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        await dispatcher.DidNotReceiveWithAnyArgs().EnqueueStepAsync(default!, default!, default!, default);
        Assert.True(await store.IsStepClaimedAsync(runId, "step1"));
    }

    private static async Task<(InMemoryFlowRunStore Store, IStepDispatcher Dispatcher, IFlowDefinition Flow, Guid RunId, ManualTimeProvider Clock)> SeedOrphanedClaimAsync()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryFlowRunStore { Clock = clock };
        var flow = MakeSingleStepFlow();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flow.Id, "Flow", runId, "manual", null, null);
        Assert.True(await store.TryRecordDispatchAsync(runId, "step1"));
        Assert.True(await store.TryClaimStepAsync(runId, "step1"));

        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.EnqueueStepAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(new ValueTask<string?>("job-1"));
        return (store, dispatcher, flow, runId, clock);
    }

    private static FlowRunRecoveryHostedService CreateRecovery(InMemoryFlowRunStore store, IStepDispatcher dispatcher, IFlowDefinition flow) =>
        new(
            store,
            [store],
            new FlowRegistry([flow]),
            new FlowGraphPlanner(),
            dispatcher,
            new InMemoryOutputsRepository(),
            Substitute.For<ILogger<FlowRunRecoveryHostedService>>(),
            new FlowRunControlOptions { OrphanedClaimTimeout = TimeSpan.FromSeconds(60) });

    /// <summary>A clock that only moves when the test says so.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        /// <summary>Moves the clock forward.</summary>
        public void Advance(TimeSpan by) => _now += by;

        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
