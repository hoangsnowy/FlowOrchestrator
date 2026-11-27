using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.Testing.Shared;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// End-to-end behaviour of the frontier continuation (#189 P0-1) on the real engine and the in-memory store:
/// a join waited on by two concurrent completions, <c>When</c> and blocked cascades, a blocked child inside a
/// loop iteration, nested loops, the concurrency limit, and the claim that a completion reads a bounded
/// number of status rows however long the run is.
/// </summary>
public sealed class ContinuationFrontierTests
{
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(30);

    private static StoreEngineRig Rig(IFlowDefinition flow, Func<IStepInstance, ValueTask<IStepResult>> execute, InMemoryFlowRunStore? store = null, IFlowRunRuntimeStore? runtime = null, IFlowRunStore? runStore = null)
    {
        store ??= new InMemoryFlowRunStore();
        return new StoreEngineRig(flow, runStore ?? store, runtime ?? store, store, new InMemoryOutputsRepository(), execute);
    }

    [Fact]
    public async Task JoinPrerequisitesCompletingConcurrently_dispatchTheJoinExactlyOnce_everyTime()
    {
        // Arrange - the write-skew case: a and b complete on two workers at the same instant, each frontier
        // read races the other's completion write. Whichever commits last must see both and dispatch the join.
        const int repetitions = 150;
        var flow = FrontierFlows.Join();
        var joinDispatches = new List<int>();

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            using var bothInHandler = new CountdownEvent(2);
            var rig = Rig(flow, step =>
            {
                if (step.Key is "a" or "b")
                {
                    bothInHandler.Signal();
                    Assert.True(bothInHandler.Wait(Rendezvous), "the prerequisites never met");
                }

                return new ValueTask<IStepResult>(FrontierFlows.Succeeded(step.Key));
            });
            var runId = await rig.TriggerAsync();

            // Act
            await rig.RunConcurrentlyAsync("a", "b");
            joinDispatches.Add(rig.DispatchCount("join"));
            await rig.DrainAsync();

            // Assert (per repetition, below)
            Assert.Equal("Succeeded", await rig.RunStatusAsync(runId));
        }

        // Assert
        Assert.All(joinDispatches, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task WhenSkip_cascadesTheBlockedSkipToEveryDependent()
    {
        // Arrange - a → b (When false) → c → d: c and d are owed blocked-skip rows, written wave by wave.
        var flow = FrontierFlows.Flow(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "Echo" },
            ["b"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection
                {
                    ["a"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@triggerBody()?.go == true" }
                }
            },
            ["c"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["b"] = [StepStatus.Succeeded] } },
            ["d"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["c"] = [StepStatus.Succeeded] } }
        });
        var rig = Rig(flow, step => new ValueTask<IStepResult>(FrontierFlows.Succeeded(step.Key)));
        var runId = await rig.TriggerAsync(new { go = false });

        // Act
        await rig.DrainAsync();

        // Assert
        Assert.Equal("Skipped", await rig.StepStatusAsync(runId, "b"));
        Assert.Equal("Skipped", await rig.StepStatusAsync(runId, "c"));
        Assert.Equal("Skipped", await rig.StepStatusAsync(runId, "d"));
        Assert.Equal(0, rig.DispatchCount("c"));
        Assert.NotEqual("Running", await rig.RunStatusAsync(runId));
    }

    [Fact]
    public async Task EntryWhenSkippedBesideAScheduledEntry_classifiesItsDependentsAtTrigger()
    {
        // Arrange - e1 is When-skipped at trigger while e2 is scheduled. No completion will ever report e1's
        // row, so the trigger itself must continue from it: f (runs after a skip) is dispatched at once, g
        // (needs Succeeded) is cascade-skipped, both before e2 has even run.
        var flow = FrontierFlows.Flow(new StepCollection
        {
            ["e1"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { [""] = new RunAfterCondition { When = "@triggerBody()?.go == true" } }
            },
            ["e2"] = new StepMetadata { Type = "Echo" },
            ["f"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["e1"] = [StepStatus.Skipped] } },
            ["g"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["e1"] = [StepStatus.Succeeded] } }
        });
        var rig = Rig(flow, step => new ValueTask<IStepResult>(FrontierFlows.Succeeded(step.Key)));

        // Act
        var runId = await rig.TriggerAsync(new { go = false });

        // Assert
        Assert.Equal(1, rig.DispatchCount("e2"));
        Assert.Equal(1, rig.DispatchCount("f"));
        Assert.Equal("Skipped", await rig.StepStatusAsync(runId, "g"));
        Assert.Equal("Running", await rig.RunStatusAsync(runId));
        await rig.DrainAsync();
        Assert.Equal("Succeeded", await rig.StepStatusAsync(runId, "f"));
        Assert.NotEqual("Running", await rig.RunStatusAsync(runId));
    }

    [Fact]
    public async Task FailedChildInsideAnIteration_blockSkipsItsSuccessor_settlesTheIteration_andAdmitsTheNext()
    {
        // Arrange - sequential loop; iteration 1's c0 fails, so its c1 is blocked. The skip is what settles
        // iteration 1, so it must free the slot for iteration 2 and, at the end, settle the loop.
        const int items = 3;
        var flow = FrontierFlows.Loop(items, limit: 1);
        var rig = Rig(flow, step => new ValueTask<IStepResult>(step.Key switch
        {
            "loop" => FrontierFlows.ParkedLoop("loop", items, 1, "c0"),
            "loop.1.c0" => new StepResult { Key = step.Key, Status = StepStatus.Failed, FailedReason = "boom" },
            _ => FrontierFlows.Succeeded(step.Key)
        }));
        var runId = await rig.TriggerAsync();

        // Act
        await rig.DrainAsync();

        // Assert
        Assert.Equal("Skipped", await rig.StepStatusAsync(runId, "loop.1.c1"));
        Assert.Equal("Succeeded", await rig.StepStatusAsync(runId, "loop.2.c1"));
        Assert.Equal("Succeeded", await rig.StepStatusAsync(runId, "loop"));
        Assert.Equal(1, rig.DispatchCount("done"));
        Assert.NotEqual("Running", await rig.RunStatusAsync(runId));
    }

    [Fact]
    public async Task NestedLoops_runEveryInnerIterationOnce_andSettleBothLevels()
    {
        // Arrange - outer(3, limit 2){ inner(2, limit 1){ x → y } } → done. An inner loop's settle is a change
        // in the outer loop's iteration, which is what settles that iteration and admits the next.
        const int outerItems = 3;
        const int innerItems = 2;
        var flow = FrontierFlows.Flow(new StepCollection
        {
            ["outer"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = Enumerable.Range(0, outerItems).Cast<object?>().ToList(),
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["inner"] = new LoopStepMetadata
                    {
                        Type = "ForEach",
                        ForEach = Enumerable.Range(0, innerItems).Cast<object?>().ToList(),
                        ConcurrencyLimit = 1,
                        Steps = new StepCollection
                        {
                            ["x"] = new StepMetadata { Type = "Echo" },
                            ["y"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["x"] = [StepStatus.Succeeded] } }
                        }
                    }
                }
            },
            ["done"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["outer"] = [StepStatus.Succeeded] } }
        });
        var executions = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var rig = Rig(flow, step =>
        {
            executions.AddOrUpdate(step.Key, 1, static (_, n) => n + 1);
            if (step.Key == "outer")
            {
                return new ValueTask<IStepResult>(FrontierFlows.ParkedLoop("outer", outerItems, 2, "inner"));
            }

            if (step.Key.EndsWith(".inner", StringComparison.Ordinal))
            {
                return new ValueTask<IStepResult>(FrontierFlows.ParkedLoop(step.Key, innerItems, 1, "x"));
            }

            return new ValueTask<IStepResult>(FrontierFlows.Succeeded(step.Key));
        });
        var runId = await rig.TriggerAsync();

        // Act
        await rig.DrainAsync(workers: 4);

        // Assert
        for (var o = 0; o < outerItems; o++)
        {
            Assert.Equal("Succeeded", await rig.StepStatusAsync(runId, $"outer.{o}.inner"));
            for (var i = 0; i < innerItems; i++)
            {
                Assert.Equal(1, executions.GetValueOrDefault($"outer.{o}.inner.{i}.x"));
                Assert.Equal(1, executions.GetValueOrDefault($"outer.{o}.inner.{i}.y"));
            }
        }

        Assert.Equal("Succeeded", await rig.StepStatusAsync(runId, "outer"));
        Assert.Equal(1, rig.DispatchCount("done"));
        Assert.Equal("Succeeded", await rig.RunStatusAsync(runId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ConcurrentWorkers_neverRunMoreIterationsThanTheLimit_andRunEachOnce(int limit)
    {
        // Arrange - an iteration counts as in flight from its c0 starting to its c1 returning; c1 returns
        // before its row is written, and admission needs that row, so the bound is exact.
        const int items = 24;
        var flow = FrontierFlows.Loop(items, limit);
        var inFlight = new ConcurrentDictionary<int, byte>();
        var peak = 0;
        var executions = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var rig = Rig(flow, async step =>
        {
            executions.AddOrUpdate(step.Key, 1, static (_, n) => n + 1);
            if (step.Key == "loop")
            {
                return FrontierFlows.ParkedLoop("loop", items, limit, "c0");
            }

            if (step.Key.StartsWith("loop.", StringComparison.Ordinal))
            {
                var index = int.Parse(step.Key.Split('.')[1]);
                if (step.Key.EndsWith(".c0", StringComparison.Ordinal))
                {
                    inFlight[index] = 0;
                    InterlockedMax(ref peak, inFlight.Count);
                }

                await Task.Yield();
                if (step.Key.EndsWith(".c1", StringComparison.Ordinal))
                {
                    inFlight.TryRemove(index, out _);
                }
            }

            return FrontierFlows.Succeeded(step.Key);
        });
        var runId = await rig.TriggerAsync();

        // Act
        await rig.DrainAsync(workers: 8);

        // Assert
        Assert.Equal("Succeeded", await rig.RunStatusAsync(runId));
        Assert.InRange(peak, 1, limit);
        for (var index = 0; index < items; index++)
        {
            Assert.Equal(1, executions.GetValueOrDefault($"loop.{index}.c0"));
            Assert.Equal(1, executions.GetValueOrDefault($"loop.{index}.c1"));
        }

        Assert.Equal(1, rig.DispatchCount("done"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public async Task LongLoop_readsTheSameNumberOfStatusRowsPerCompletion_atAnySize(int limit)
    {
        // Arrange - the #189 P0-1 pass condition: rows read per step completion must not grow with n.
        // Before the frontier read this was ~2n rows per completion (2 003 at 1 000 items).
        var small = await CountReadsAsync(items: 100, limit);

        // Act
        var large = await CountReadsAsync(items: 1000, limit);

        // Assert - per-completion rows flat within a small constant, and the whole-run status map read only
        // when the run closes (a constant number of times, not once per completion).
        Assert.True(
            large.RowsPerCompletion <= (small.RowsPerCompletion * 1.25) + 1,
            $"status rows per completion grew from {small.RowsPerCompletion:F1} at 100 items to {large.RowsPerCompletion:F1} at 1000.");
        Assert.True(large.RowsPerCompletion < 16, $"{large.RowsPerCompletion:F1} status rows per completion");
        Assert.InRange(large.FullReads, 1, 3);
        Assert.InRange(small.FullReads, 1, 3);
    }

    private static async Task<(double RowsPerCompletion, int FullReads)> CountReadsAsync(int items, int limit)
    {
        var store = new InMemoryFlowRunStore();
        var counting = new CountingRuntimeStore(store);
        var executions = 0;
        var rig = Rig(FrontierFlows.Loop(items, limit), step =>
        {
            Interlocked.Increment(ref executions);
            return new ValueTask<IStepResult>(step.Key == "loop"
                ? FrontierFlows.ParkedLoop("loop", items, limit, "c0")
                : FrontierFlows.Succeeded(step.Key));
        }, store, counting);
        var runId = await rig.TriggerAsync();
        await rig.DrainAsync();
        Assert.Equal("Succeeded", await rig.RunStatusAsync(runId));
        return ((double)counting.StatusRows / executions, counting.FullReads);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>Counts the rows every status read returns and how often the whole run is read.</summary>
    private sealed class CountingRuntimeStore(InMemoryFlowRunStore inner) : IFlowRunRuntimeStore
    {
        private long _statusRows;
        private int _fullReads;

        public long StatusRows => Interlocked.Read(ref _statusRows);

        public int FullReads => Volatile.Read(ref _fullReads);

        public async Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId)
        {
            var statuses = await inner.GetStepStatusesAsync(runId);
            Interlocked.Increment(ref _fullReads);
            Interlocked.Add(ref _statusRows, statuses.Count);
            return statuses;
        }

        public async Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
        {
            var statuses = await inner.GetStepStatusesAsync(runId, stepKeys);
            Interlocked.Add(ref _statusRows, statuses.Count);
            return statuses;
        }

        public Task<bool> HasRunningOrPendingStepAsync(Guid runId) => inner.HasRunningOrPendingStepAsync(runId);

        public Task<int> CountSettledIterationsAsync(Guid runId, string loopStepKey, int iterations, IReadOnlyCollection<string> bodyStepKeys)
            => inner.CountSettledIterationsAsync(runId, loopStepKey, iterations, bodyStepKeys);

        public Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId) => inner.GetClaimedStepKeysAsync(runId);

        public Task<bool> IsStepClaimedAsync(Guid runId, string stepKey) => inner.IsStepClaimedAsync(runId, stepKey);

        public Task<bool> TryClaimStepAsync(Guid runId, string stepKey) => inner.TryClaimStepAsync(runId, stepKey);

        public Task ReleaseStepClaimAsync(Guid runId, string stepKey) => inner.ReleaseStepClaimAsync(runId, stepKey);

        public Task<IReadOnlyList<string>> ReleaseOrphanedClaimsAsync(Guid runId, TimeSpan minimumAge)
            => inner.ReleaseOrphanedClaimsAsync(runId, minimumAge);

        public Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
            => inner.RecordSkippedStepAsync(runId, stepKey, stepType, reason);

        public Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason, string? evaluationTraceJson)
            => inner.RecordSkippedStepAsync(runId, stepKey, stepType, reason, evaluationTraceJson);

        public Task<string?> GetRunStatusAsync(Guid runId) => inner.GetRunStatusAsync(runId);

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
    }
}
