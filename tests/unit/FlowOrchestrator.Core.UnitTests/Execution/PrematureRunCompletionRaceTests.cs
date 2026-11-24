using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Deterministic reproduction of a premature run completion seen in the multi-replica stress harness:
/// two parallel branches <c>a</c> and <c>b</c> feed a join step, and the continuation of <c>a</c>
/// completes the run while the continuation of <c>b</c> is about to dispatch the join.
/// </summary>
/// <remarks>
/// Interleaving forced by the gates below:
/// <list type="number">
///   <item><c>a</c> completes; its continuation reads statuses (<c>b</c> still Running), so the join is only "waiting" and nothing is enqueued.</item>
///   <item><c>b</c> completes and writes <c>Succeeded</c>, but its continuation has not yet recorded the join's dispatch-ledger row.</item>
///   <item><c>a</c>'s continuation re-reads statuses for the completion gate: nothing in flight, no claim or ledger row without a status row — so it completes the run.</item>
///   <item><c>b</c>'s continuation then dispatches the join into a run that is already terminal.</item>
/// </list>
/// The completion gate never re-evaluates the graph on the fresh status map, so a step that has become
/// ready but is not yet dispatched is invisible to it.
/// </remarks>
public sealed class PrematureRunCompletionRaceTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);
    private static readonly AsyncLocal<string?> Caller = new();

    private static IFlowDefinition MakeJoinFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "Echo" },
                ["b"] = new StepMetadata { Type = "Echo" },
                ["join"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection
                    {
                        ["a"] = [StepStatus.Succeeded],
                        ["b"] = [StepStatus.Succeeded]
                    }
                },
                ["end"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["join"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    [Fact]
    public async Task ParallelBranches_CompletingConcurrently_RunIsNotCompletedBeforeJoinIsDispatched()
    {
        // Arrange
        var bHandlerEntered = new ManualResetEventSlim(false);
        var bHandlerRelease = new ManualResetEventSlim(false);
        var bInContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);


        IStepResult ResultFor(string key)
        {
            if (key == "b")
            {
                bHandlerEntered.Set();
                Assert.True(bHandlerRelease.Wait(GateTimeout), "b's handler was never released.");
            }

            return new StepResult { Key = key, Status = StepStatus.Succeeded, Result = new { ok = true } };
        }

        var harness = new LoopBarrierEngineHarness(
            MakeJoinFlow(),
            ResultFor,
            runtimeStore: store => new GatedStatusStore(store, async (caller, callIndex) =>
            {
                if (caller == "a" && callIndex == 2)
                {
                    // Before a's completion-gate re-read: let b finish its handler and write Succeeded,
                    // and wait until b's continuation has started (its status row is now terminal).
                    bHandlerRelease.Set();
                    await bInContinuation.Task.WaitAsync(GateTimeout);
                }
                else if (caller == "b" && callIndex == 1)
                {
                    // b's continuation holds here — it has not yet recorded the join's dispatch row.
                    bInContinuation.TrySetResult();
                    await aDone.Task.WaitAsync(GateTimeout);
                }
            }));
        var runId = await harness.TriggerAsync();

        var bTask = Task.Run(async () =>
        {
            Caller.Value = "b";
            await harness.RedeliverAsync(runId, "b", "Echo");
        });
        Assert.True(bHandlerEntered.Wait(GateTimeout), "b never started.");

        // Act
        await Task.Run(async () =>
        {
            Caller.Value = "a";
            await harness.RedeliverAsync(runId, "a", "Echo");
        });
        var statusWhenADone = await harness.RunStatusAsync(runId);
        aDone.TrySetResult();
        await bTask.WaitAsync(GateTimeout);

        // Assert
        Assert.Contains("join", harness.Enqueued);
        Assert.True(
            statusWhenADone == "Running",
            $"Run was completed as '{statusWhenADone}' by a's continuation while 'join' was ready but not yet dispatched by b's continuation.");
    }

    /// <summary>
    /// Runtime-store decorator that invokes a hook before every <see cref="GetStepStatusesAsync"/> call,
    /// passing the caller label carried by <see cref="Caller"/> and that caller's 1-based call index.
    /// </summary>
    private sealed class GatedStatusStore(InMemoryFlowRunStore inner, Func<string?, int, Task> hook) : IFlowRunRuntimeStore
    {
        private int _aCalls;
        private int _bCalls;

        public async Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId)
        {
            var caller = Caller.Value;
            var index = caller switch
            {
                "a" => Interlocked.Increment(ref _aCalls),
                "b" => Interlocked.Increment(ref _bCalls),
                _ => 0
            };
            await hook(caller, index);
            return await inner.GetStepStatusesAsync(runId);
        }

        public Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId) => inner.GetClaimedStepKeysAsync(runId);

        public Task<bool> IsStepClaimedAsync(Guid runId, string stepKey) => inner.IsStepClaimedAsync(runId, stepKey);

        public Task<bool> TryClaimStepAsync(Guid runId, string stepKey) => inner.TryClaimStepAsync(runId, stepKey);

        public Task ReleaseStepClaimAsync(Guid runId, string stepKey) => inner.ReleaseStepClaimAsync(runId, stepKey);

        public Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
            => inner.RecordSkippedStepAsync(runId, stepKey, stepType, reason);

        public Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason, string? evaluationTraceJson)
            => inner.RecordSkippedStepAsync(runId, stepKey, stepType, reason, evaluationTraceJson);

        public Task<string?> GetRunStatusAsync(Guid runId) => inner.GetRunStatusAsync(runId);

        public Task<IReadOnlyList<string>> ReleaseOrphanedClaimsAsync(Guid runId, TimeSpan minimumAge)
            => inner.ReleaseOrphanedClaimsAsync(runId, minimumAge);
    }
}
