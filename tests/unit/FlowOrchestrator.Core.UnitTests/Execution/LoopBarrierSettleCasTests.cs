using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// The ForEach barrier settle is a compare-and-swap (RFC 2.0 P-1(a)): when two continuations decide
/// the same loop at the same moment, only the one whose <c>Running → Succeeded</c> write wins publishes
/// the loop's <see cref="StepCompletedEvent"/> and continues the graph from it.
/// </summary>
public sealed class LoopBarrierSettleCasTests
{
    /// <summary>Generous budget so a contended CI box cannot trip the rendezvous.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    private static IFlowDefinition MakeLoopFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["loop"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = new List<object?> { "a", "b" },
                    ConcurrencyLimit = 2,
                    Steps = new StepCollection { ["work"] = new StepMetadata { Type = "Echo" } }
                },
                ["after_loop"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    private static IStepResult StepResultFor(string stepKey) => stepKey == "loop"
        ? new StepResult
        {
            Key = "loop",
            Status = StepStatus.Running,
            Result = new { iterations = 2 },
            DispatchHint = new StepDispatchHint(
            [
                new StepDispatchRequest("loop.0.work", "Echo", new Dictionary<string, object?>()),
                new StepDispatchRequest("loop.1.work", "Echo", new Dictionary<string, object?>())
            ])
        }
        : new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };

    [Fact]
    public async Task TwoContinuationsDecidingTheSameLoop_publishTheLoopCompletionOnce()
    {
        // Arrange - the gate holds each child's first status read (the top of its continuation) until
        // BOTH children have recorded their completion, then holds both again until both reads are
        // taken. Both continuations therefore see "all iterations terminal, loop Running" and both
        // reach the settle write: the exact double-settle interleaving, with no timing involved.
        var notifier = new StepCompletedRecorder();
        GatedRuntimeStore? gated = null;
        var harness = new LoopBarrierEngineHarness(
            MakeLoopFlow(),
            StepResultFor,
            eventNotifier: notifier,
            runtimeStore: store => gated = new GatedRuntimeStore(store));

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");
        gated!.Arm(participants: 2);

        // Act
        await harness.RunConcurrentlyAsync("loop.0.work", "loop.1.work").WaitAsync(GateTimeout);
        await harness.DrainAsync();

        // Assert
        Assert.True(gated.BothReadStaleView, "the gate must have forced both continuations to read before either settled");
        Assert.Equal(1, notifier.CountFor("loop"));
        Assert.Equal(1, harness.Enqueued.Count(k => k == "after_loop"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task SettleAsync_calledTwiceOffTheSameStaleView_settlesTheLoopOnlyOnce()
    {
        // Arrange - a parked loop whose two iterations are terminal; both callers hold the same
        // status map, read before either of them settled the loop.
        var flow = MakeLoopFlow();
        var runId = Guid.NewGuid();
        var store = new InMemoryFlowRunStore();
        var outputs = Substitute.For<IOutputsRepository>();
        outputs.GetStepOutputAsync(runId, "loop")
            .Returns(new ValueTask<object?>(JsonSerializer.SerializeToElement(new { iterations = 2 })));
        await store.StartRunAsync(flow.Id, "LoopFlow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);
        var staleView = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Running,
            ["loop.0.work"] = StepStatus.Succeeded,
            ["loop.1.work"] = StepStatus.Succeeded
        };

        // Act
        var first = await LoopBarrier.SettleAsync(flow, runId, ["loop"], staleView, outputs, store);
        var second = await LoopBarrier.SettleAsync(flow, runId, ["loop"], staleView, outputs, store);

        // Assert
        Assert.Equal(["loop"], first);
        Assert.Empty(second);
        Assert.Equal(StepStatus.Succeeded, (await store.GetStepStatusesAsync(runId))["loop"]);
    }

    [Fact]
    public async Task SettleAsync_onALoopAbandonedByRunControl_doesNotOverwriteTheSkip()
    {
        // Arrange - run control abandoned the loop (Skipped) after the caller read its view.
        var flow = MakeLoopFlow();
        var runId = Guid.NewGuid();
        var store = new InMemoryFlowRunStore();
        var outputs = Substitute.For<IOutputsRepository>();
        outputs.GetStepOutputAsync(runId, "loop")
            .Returns(new ValueTask<object?>(JsonSerializer.SerializeToElement(new { iterations = 1 })));
        await store.StartRunAsync(flow.Id, "LoopFlow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);
        await store.RecordStepCompleteAsync(runId, "loop", StepStatus.Skipped.ToString(), null, "Run is Cancelled.");
        var staleView = new Dictionary<string, StepStatus>(StringComparer.Ordinal)
        {
            ["loop"] = StepStatus.Running,
            ["loop.0.work"] = StepStatus.Succeeded
        };

        // Act
        var settled = await LoopBarrier.SettleAsync(flow, runId, ["loop"], staleView, outputs, store);

        // Assert
        Assert.Empty(settled);
        Assert.Equal(StepStatus.Skipped, (await store.GetStepStatusesAsync(runId))["loop"]);
    }

    /// <summary>Counts <see cref="StepCompletedEvent"/>s per step key.</summary>
    private sealed class StepCompletedRecorder : IFlowEventNotifier
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        /// <summary>Number of completion events published for <paramref name="stepKey"/>.</summary>
        public int CountFor(string stepKey)
        {
            lock (_counts)
            {
                return _counts.GetValueOrDefault(stepKey);
            }
        }

        /// <inheritdoc/>
        public ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default)
        {
            if (evt is StepCompletedEvent completed)
            {
                lock (_counts)
                {
                    _counts[completed.StepKey] = _counts.GetValueOrDefault(completed.StepKey) + 1;
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Runtime store decorator whose first <c>participants</c> status reads after <see cref="Arm"/> form a
    /// two-phase rendezvous: nobody reads until everyone has arrived, and nobody continues until everyone
    /// has read. Every other member forwards to the real store.
    /// </summary>
    private sealed class GatedRuntimeStore(InMemoryFlowRunStore inner) : IFlowRunRuntimeStore
    {
        private TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _allRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _participants;
        private int _arrived;
        private int _read;
        private int _staleRunning;

        /// <summary>Whether every gated read still saw the loop <c>Running</c> with both children terminal.</summary>
        public bool BothReadStaleView => Volatile.Read(ref _staleRunning) == _participants;

        /// <summary>Gates the next <paramref name="participants"/> status reads.</summary>
        public void Arm(int participants)
        {
            _allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _allRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _arrived = 0;
            _read = 0;
            _staleRunning = 0;
            Volatile.Write(ref _participants, participants);
        }

        public Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId)
            => GatedReadAsync(runId, () => inner.GetStepStatusesAsync(runId));

        // The continuation's first read is the loop advance's keyed read of the loop row and its iterations,
        // so the keyed overload goes through the same gate as the full one.
        public Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
            => GatedReadAsync(runId, () => inner.GetStepStatusesAsync(runId, stepKeys));

        public Task<bool> HasRunningOrPendingStepAsync(Guid runId) => inner.HasRunningOrPendingStepAsync(runId);

        public Task<int> CountSettledIterationsAsync(Guid runId, string loopStepKey, int iterations, IReadOnlyCollection<string> bodyStepKeys)
            => inner.CountSettledIterationsAsync(runId, loopStepKey, iterations, bodyStepKeys);

        private async Task<IReadOnlyDictionary<string, StepStatus>> GatedReadAsync(Guid runId, Func<Task<IReadOnlyDictionary<string, StepStatus>>> read)
        {
            var participants = Volatile.Read(ref _participants);
            var arrival = participants == 0 ? int.MaxValue : Interlocked.Increment(ref _arrived);
            if (arrival > participants)
            {
                return await read();
            }

            if (arrival == participants)
            {
                _allArrived.TrySetResult();
            }

            await _allArrived.Task.WaitAsync(GateTimeout);
            var statuses = await read();
            // A keyed read returns only the rows it asked for, so judge "both children terminal" on the store
            // itself: what matters is that this read saw the loop Running after both completions committed.
            var run = await inner.GetStepStatusesAsync(runId);
            if (statuses.GetValueOrDefault("loop") == StepStatus.Running
                && run.GetValueOrDefault("loop.0.work") == StepStatus.Succeeded
                && run.GetValueOrDefault("loop.1.work") == StepStatus.Succeeded)
            {
                Interlocked.Increment(ref _staleRunning);
            }

            if (Interlocked.Increment(ref _read) == participants)
            {
                _allRead.TrySetResult();
            }

            await _allRead.Task.WaitAsync(GateTimeout);
            return statuses;
        }

        public Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId) => inner.GetClaimedStepKeysAsync(runId);

        public Task<bool> IsStepClaimedAsync(Guid runId, string stepKey) => inner.IsStepClaimedAsync(runId, stepKey);

        public Task<bool> TryClaimStepAsync(Guid runId, string stepKey) => inner.TryClaimStepAsync(runId, stepKey);

        public Task ReleaseStepClaimAsync(Guid runId, string stepKey) => inner.ReleaseStepClaimAsync(runId, stepKey);

        public Task<StepLease?> TryAcquireStepLeaseAsync(Guid runId, string stepKey, string ownerId, TimeSpan duration)
            => inner.TryAcquireStepLeaseAsync(runId, stepKey, ownerId, duration);

        public Task<IReadOnlyList<StepLease>> RenewStepLeasesAsync(IReadOnlyList<StepLease> leases, TimeSpan duration)
            => inner.RenewStepLeasesAsync(leases, duration);

        public Task<bool> ReleaseStepLeaseAsync(StepLease lease) => inner.ReleaseStepLeaseAsync(lease);

        public Task<bool> PinStepLeaseAsync(StepLease lease) => inner.PinStepLeaseAsync(lease);

        public Task<bool> TryRecordLeasedStepCompleteAsync(StepLease lease, string status, string? outputJson, string? errorMessage)
            => inner.TryRecordLeasedStepCompleteAsync(lease, status, outputJson, errorMessage);

        public Task<IReadOnlyList<ExpiredStepLease>> LeaseExpiredStepLeasesAsync(TimeSpan redeliveryBackoff, int maxCount)
            => inner.LeaseExpiredStepLeasesAsync(redeliveryBackoff, maxCount);

        public Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
            => inner.RecordSkippedStepAsync(runId, stepKey, stepType, reason);

        public Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason, string? evaluationTraceJson)
            => inner.RecordSkippedStepAsync(runId, stepKey, stepType, reason, evaluationTraceJson);

        public Task<string?> GetRunStatusAsync(Guid runId) => inner.GetRunStatusAsync(runId);

        public Task<IReadOnlyList<string>> ReleaseOrphanedClaimsAsync(Guid runId, TimeSpan minimumAge)
            => inner.ReleaseOrphanedClaimsAsync(runId, minimumAge);
    }
}
