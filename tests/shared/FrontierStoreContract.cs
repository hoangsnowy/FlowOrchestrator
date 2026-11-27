using System.Collections.Concurrent;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Testing.Shared;

/// <summary>The four stores the engine needs, as one backend provides them.</summary>
/// <param name="Run">Run store.</param>
/// <param name="Runtime">Runtime store.</param>
/// <param name="Control">Run-control store.</param>
/// <param name="Outputs">Outputs repository.</param>
internal sealed record FrontierStores(IFlowRunStore Run, IFlowRunRuntimeStore Runtime, IFlowRunControlStore Control, IOutputsRepository Outputs);

/// <summary>
/// The store contract behind the frontier continuation (#189 P0-1) — the keyed status read, the open-row
/// existence check and the settled-iteration count — plus the engine scenarios that depend on their
/// visibility guarantee, run unchanged against every built-in store.
/// </summary>
/// <remarks>
/// Each backend derives a concrete class that supplies <see cref="CreateStores"/>. The concurrency scenarios
/// are the write-skew rule of RFC 2.0 §3.5 on a real database: two completions that unlock one join must
/// never both miss it, and a ForEach must never run more iterations than its limit or settle early.
/// </remarks>
public abstract class FrontierStoreContract
{
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(30);

    /// <summary>Repetitions of the concurrent-join scenario; enough to land both orderings many times.</summary>
    protected virtual int JoinRepetitions => 40;

    /// <summary>Creates the backend's stores over a migrated database.</summary>
    internal abstract FrontierStores CreateStores();

    [Fact]
    public async Task KeyedStatusRead_returnsExactlyTheRequestedKeysThatHaveARow()
    {
        // Arrange
        var stores = CreateStores();
        var runId = Guid.NewGuid();
        var otherRun = Guid.NewGuid();
        await stores.Run.StartRunAsync(Guid.NewGuid(), "Frontier", runId, "manual", null, null);
        await stores.Run.StartRunAsync(Guid.NewGuid(), "Frontier", otherRun, "manual", null, null);
        await stores.Run.RecordStepStartAsync(runId, "a", "T", null, null);
        await stores.Run.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
        await stores.Run.RecordStepStartAsync(runId, "loop.3.child", "T", null, null);
        await stores.Run.RecordStepStartAsync(runId, "unrelated", "T", null, null);
        await stores.Run.RecordStepStartAsync(otherRun, "b", "T", null, null);

        // Act
        var statuses = await stores.Runtime.GetStepStatusesAsync(runId, ["a", "a", "b", "loop.3.child", "missing"]);
        var none = await stores.Runtime.GetStepStatusesAsync(runId, Array.Empty<string>());

        // Assert
        Assert.Equal(2, statuses.Count);
        Assert.Equal(StepStatus.Succeeded, statuses["a"]);
        Assert.Equal(StepStatus.Running, statuses["loop.3.child"]);
        Assert.False(statuses.ContainsKey("b"));
        Assert.Empty(none);
    }

    [Fact]
    public async Task KeyedStatusRead_ofMoreKeysThanOneStatementBinds_returnsThemAll()
    {
        // Arrange - SQL Server binds at most 2 100 parameters per statement.
        var stores = CreateStores();
        var runId = Guid.NewGuid();
        await stores.Run.StartRunAsync(Guid.NewGuid(), "Frontier", runId, "manual", null, null);
        var keys = Enumerable.Range(0, 2500).Select(i => $"loop.{i}.child").ToArray();
        foreach (var key in keys.Where((_, i) => i % 500 == 0))
        {
            await stores.Run.RecordStepStartAsync(runId, key, "T", null, null);
        }

        // Act
        var statuses = await stores.Runtime.GetStepStatusesAsync(runId, keys);

        // Assert
        Assert.Equal(5, statuses.Count);
        Assert.True(statuses.ContainsKey("loop.2000.child"));
    }

    [Fact]
    public async Task HasRunningOrPendingStep_followsEveryOpeningAndClosingWrite()
    {
        // Arrange
        var stores = CreateStores();
        var runId = Guid.NewGuid();
        await stores.Run.StartRunAsync(Guid.NewGuid(), "Frontier", runId, "manual", null, null);

        // Act
        var empty = await stores.Runtime.HasRunningOrPendingStepAsync(runId);
        await stores.Run.RecordStepStartAsync(runId, "a", "T", null, null);
        var running = await stores.Runtime.HasRunningOrPendingStepAsync(runId);
        await stores.Run.RecordStepCompleteAsync(runId, "a", "Pending", null, null);
        var pending = await stores.Runtime.HasRunningOrPendingStepAsync(runId);
        await stores.Run.RecordStepCompleteAsync(runId, "a", "Failed", null, "boom");
        var failed = await stores.Runtime.HasRunningOrPendingStepAsync(runId);
        var retried = await stores.Run.RetryStepAsync(runId, "a");
        var afterRetry = await stores.Runtime.HasRunningOrPendingStepAsync(runId);
        await stores.Run.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
        await stores.Runtime.RecordSkippedStepAsync(runId, "b", "T", "unmet");
        var closed = await stores.Runtime.HasRunningOrPendingStepAsync(runId);
        var otherRun = await stores.Runtime.HasRunningOrPendingStepAsync(Guid.NewGuid());

        // Assert
        Assert.False(empty);
        Assert.True(running);
        Assert.True(pending);
        Assert.False(failed);
        Assert.True(retried);
        Assert.True(afterRetry);
        Assert.False(closed);
        Assert.False(otherRun);
    }

    [Fact]
    public async Task CountSettledIterations_countsOnlyIterationsWhoseWholeBodyIsTerminal()
    {
        // Arrange - body {a, b}, 4 iterations.
        var stores = CreateStores();
        var runId = Guid.NewGuid();
        await stores.Run.StartRunAsync(Guid.NewGuid(), "Frontier", runId, "manual", null, null);
        async Task RowAsync(string key, string status)
        {
            await stores.Run.RecordStepStartAsync(runId, key, "T", null, null);
            if (status != "Running")
            {
                await stores.Run.RecordStepCompleteAsync(runId, key, status, null, null);
            }
        }

        await RowAsync("my_loop.0.a", "Succeeded");   // iteration 0: settled
        await RowAsync("my_loop.0.b", "Succeeded");
        await RowAsync("my_loop.1.a", "Succeeded");   // iteration 1: b still running
        await RowAsync("my_loop.1.b", "Running");
        await RowAsync("my_loop.2.a", "Failed");      // iteration 2: settled (failed + blocked-skip)
        await RowAsync("my_loop.2.b", "Skipped");
        await RowAsync("my_loop.3.a", "Succeeded");   // iteration 3: b has no row yet
        await RowAsync("my_loop.0.inner.0.a", "Succeeded"); // a nested loop's row is not a body row
        await RowAsync("my_loop.0.inner.0.b", "Succeeded");
        await RowAsync("my_loop.01.a", "Succeeded");  // not a canonical index
        await RowAsync("my_loop.01.b", "Succeeded");
        await RowAsync("my_loop.9.a", "Succeeded");   // beyond the iteration count
        await RowAsync("my_loop.9.b", "Succeeded");
        await RowAsync("myXloop.1.a", "Succeeded");   // '_' must not match as a LIKE wildcard
        await RowAsync("myXloop.1.b", "Succeeded");
        await RowAsync("my_loop2.3.a", "Succeeded");  // a sibling loop with a longer key
        await RowAsync("my_loop2.3.b", "Succeeded");
        await RowAsync("my_loop..a", "Succeeded");    // an empty iteration segment is not iteration 0
        await RowAsync("my_loop.+3.b", "Succeeded");  // nor is a signed one iteration 3

        // Act
        var settled = await stores.Runtime.CountSettledIterationsAsync(runId, "my_loop", 4, ["a", "b"]);
        var oneChildBody = await stores.Runtime.CountSettledIterationsAsync(runId, "my_loop", 4, ["a"]);
        var nested = await stores.Runtime.CountSettledIterationsAsync(runId, "my_loop.0.inner", 1, ["a", "b"]);
        var noIterations = await stores.Runtime.CountSettledIterationsAsync(runId, "my_loop", 0, ["a", "b"]);
        var otherRun = await stores.Runtime.CountSettledIterationsAsync(Guid.NewGuid(), "my_loop", 4, ["a", "b"]);

        // Assert
        Assert.Equal(2, settled);
        Assert.Equal(4, oneChildBody);
        Assert.Equal(1, nested);
        Assert.Equal(0, noIterations);
        Assert.Equal(0, otherRun);
    }

    [Fact]
    public async Task JoinPrerequisitesCompletingConcurrently_dispatchTheJoinExactlyOnce_neverZero()
    {
        // Arrange - the write-skew rule on a real store: both completion writes land at the same instant and
        // each frontier read races the other's commit. The last committer must see both and dispatch the join;
        // the dispatch ledger must absorb the case where both do.
        var stores = CreateStores();
        var flow = FrontierFlows.Join();
        var counts = new List<int>();

        for (var repetition = 0; repetition < JoinRepetitions; repetition++)
        {
            using var bothInHandler = new CountdownEvent(2);
            var rig = new StoreEngineRig(flow, stores.Run, stores.Runtime, stores.Control, stores.Outputs, step =>
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
            counts.Add(rig.DispatchCount("join"));
            await rig.DrainAsync();
            Assert.Equal("Succeeded", await rig.RunStatusAsync(runId));
        }

        // Assert
        Assert.All(counts, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ForEachOnConcurrentWorkers_respectsTheLimit_runsEveryIterationOnce_andSettlesOnce(int limit)
    {
        // Arrange
        const int items = 16;
        var stores = CreateStores();
        var flow = FrontierFlows.Loop(items, limit);
        var inFlight = new ConcurrentDictionary<int, byte>();
        var peak = 0;
        var executions = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var rig = new StoreEngineRig(flow, stores.Run, stores.Runtime, stores.Control, stores.Outputs, async step =>
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
                    var count = inFlight.Count;
                    int seen;
                    while ((seen = Volatile.Read(ref peak)) < count && Interlocked.CompareExchange(ref peak, count, seen) != seen)
                    {
                    }
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
        await rig.DrainAsync(workers: 6);

        // Assert
        Assert.Equal("Succeeded", await rig.RunStatusAsync(runId));
        Assert.Equal("Succeeded", await rig.StepStatusAsync(runId, "loop"));
        Assert.InRange(peak, 1, limit);
        for (var index = 0; index < items; index++)
        {
            Assert.Equal(1, executions.GetValueOrDefault($"loop.{index}.c0"));
            Assert.Equal(1, executions.GetValueOrDefault($"loop.{index}.c1"));
        }

        Assert.Equal(1, rig.DispatchCount("done"));
        Assert.Equal(1, executions.GetValueOrDefault("done"));
    }
}
