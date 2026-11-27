using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Step execution leases (RFC 2.0 C1) on a real PostgreSQL: acquisition, takeover of an expired lease, the
/// fence on every write of the lease's attempt, renewal, pinning, and the expired-lease sweep — including the
/// races a takeover runs against a concurrent completion and against other takeovers.
/// </summary>
/// <remarks>
/// Expiry is simulated by moving the lease's <c>expires_at</c> into the past on the database clock, so no test
/// waits for wall time. The database is shared with the other store tests: every assertion is scoped to this
/// test's run.
/// </remarks>
public sealed partial class PostgreSqlFlowRunStoreTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private async Task<Guid> StartLeaseRunAsync()
    {
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(Guid.NewGuid(), "LeaseFlow", runId, "manual", null, null);
        return runId;
    }

    private async Task ExpireLeasesAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE flow_step_leases SET expires_at = clock_timestamp() - interval '2 minutes' WHERE run_id = @runId AND expires_at IS NOT NULL",
            new { runId });
    }

    private async Task AgeRedeliveriesAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE flow_step_leases SET redelivered_at = clock_timestamp() - interval '5 minutes' WHERE run_id = @runId AND redelivered_at IS NOT NULL",
            new { runId });
    }

    [Fact]
    public async Task TryAcquireStepLeaseAsync_grants_fence_1_once_and_refuses_a_live_lease()
    {
        // Arrange
        var runId = await StartLeaseRunAsync();

        // Act
        var first = await _store.TryAcquireStepLeaseAsync(runId, "work", "a", Lease);
        var second = await _store.TryAcquireStepLeaseAsync(runId, "work", "b", Lease);

        // Assert
        Assert.Equal(new StepLease(runId, "work", 1), first);
        Assert.Null(second);
        Assert.True(await _store.IsStepClaimedAsync(runId, "work"));
    }

    [Fact]
    public async Task TryAcquireStepLeaseAsync_takes_over_an_expired_lease_only_while_the_step_is_not_terminal()
    {
        // Arrange — three dead attempts: never started, Running, Pending; one that completed; one permanent claim.
        var runId = await StartLeaseRunAsync();
        foreach (var key in new[] { "unstarted", "running", "pending", "done" })
        {
            await _store.TryAcquireStepLeaseAsync(runId, key, "dead", Lease);
        }

        await _store.RecordStepStartAsync(runId, "running", "Work", null, null);
        await _store.RecordStepStartAsync(runId, "pending", "Poll", null, null);
        await _store.RecordStepCompleteAsync(runId, "pending", "Pending", null, null);
        await _store.RecordStepStartAsync(runId, "done", "Work", null, null);
        await _store.RecordStepCompleteAsync(runId, "done", "Succeeded", null, null);
        await _store.TryClaimStepAsync(runId, "skipclaim");
        await ExpireLeasesAsync(runId);

        // Act
        var taken = new Dictionary<string, StepLease?>();
        foreach (var key in new[] { "unstarted", "running", "pending", "done", "skipclaim" })
        {
            taken[key] = await _store.TryAcquireStepLeaseAsync(runId, key, "next", Lease);
        }

        // Assert
        Assert.Equal(2, taken["unstarted"]?.Fence);
        Assert.Equal(2, taken["running"]?.Fence);
        Assert.Equal(2, taken["pending"]?.Fence);
        Assert.Null(taken["done"]);
        Assert.Null(taken["skipclaim"]);
    }

    [Fact]
    public async Task TryRecordLeasedStepCompleteAsync_rejects_a_superseded_fence_and_a_terminal_completion_ends_the_lease()
    {
        // Arrange — attempt 1 stalls, attempt 2 takes over.
        var runId = await StartLeaseRunAsync();
        var stale = (await _store.TryAcquireStepLeaseAsync(runId, "work", "a", Lease))!;
        await _store.RecordStepStartAsync(runId, "work", "Work", null, null);
        await ExpireLeasesAsync(runId);
        var current = (await _store.TryAcquireStepLeaseAsync(runId, "work", "b", Lease))!;

        // Act
        var staleWrite = await _store.TryRecordLeasedStepCompleteAsync(stale, "Failed", null, "stale");
        var currentWrite = await _store.TryRecordLeasedStepCompleteAsync(current, "Succeeded", "{\"ok\":true}", null);
        await ExpireLeasesAsync(runId);
        var afterTerminal = await _store.TryAcquireStepLeaseAsync(runId, "work", "c", Lease);

        // Assert
        Assert.False(staleWrite);
        Assert.True(currentWrite);
        Assert.Equal("Succeeded", (await _store.GetStepAsync(runId, "work"))?.Status);
        Assert.Null(afterTerminal);
    }

    [Fact]
    public async Task ReleaseStepLeaseAsync_is_fenced_and_the_next_attempt_gets_a_higher_fence()
    {
        // Arrange — a Pending poll hands its step on; a stale attempt tries the same release.
        var runId = await StartLeaseRunAsync();
        var first = (await _store.TryAcquireStepLeaseAsync(runId, "poll", "a", Lease))!;

        // Act
        var wrongFence = await _store.ReleaseStepLeaseAsync(first with { Fence = 99 });
        var released = await _store.ReleaseStepLeaseAsync(first);
        var claimedAfterRelease = await _store.IsStepClaimedAsync(runId, "poll");
        var next = await _store.TryAcquireStepLeaseAsync(runId, "poll", "b", Lease);
        var staleAfterReacquire = await _store.TryRecordLeasedStepCompleteAsync(first, "Failed", null, null);

        // Assert
        Assert.False(wrongFence);
        Assert.True(released);
        Assert.False(claimedAfterRelease);
        Assert.Equal(2, next?.Fence);
        Assert.False(staleAfterReacquire);
    }

    [Fact]
    public async Task PinStepLeaseAsync_makes_the_claim_permanent()
    {
        // Arrange — a loop step parked on its barrier.
        var runId = await StartLeaseRunAsync();
        var lease = (await _store.TryAcquireStepLeaseAsync(runId, "loop", "a", Lease))!;
        await _store.RecordStepStartAsync(runId, "loop", "ForEach", null, null);

        // Act
        var pinned = await _store.PinStepLeaseAsync(lease);
        await ExpireLeasesAsync(runId);
        var takeover = await _store.TryAcquireStepLeaseAsync(runId, "loop", "b", Lease);
        var swept = (await _store.LeaseExpiredStepLeasesAsync(Lease, 1000)).Where(e => e.RunId == runId).ToList();

        // Assert
        Assert.True(pinned);
        Assert.Null(takeover);
        Assert.Empty(swept);
    }

    [Fact]
    public async Task RenewStepLeasesAsync_renews_current_leases_and_drops_superseded_ones()
    {
        // Arrange
        var runId = await StartLeaseRunAsync();
        var kept = (await _store.TryAcquireStepLeaseAsync(runId, "kept", "a", Lease))!;
        var lost = (await _store.TryAcquireStepLeaseAsync(runId, "lost", "a", Lease))!;
        await _store.RecordStepStartAsync(runId, "lost", "Work", null, null);
        await ExpireLeasesAsync(runId);
        await _store.TryAcquireStepLeaseAsync(runId, "lost", "b", Lease);

        // Act
        var renewed = await _store.RenewStepLeasesAsync([kept, lost], Lease);
        var takeoverOfKept = await _store.TryAcquireStepLeaseAsync(runId, "kept", "b", Lease);

        // Assert — the renewal pushed "kept" back into the future, so nobody can take it over.
        Assert.Equal([kept], renewed);
        Assert.Null(takeoverOfKept);
    }

    [Fact]
    public async Task LeaseExpiredStepLeasesAsync_selects_expired_non_terminal_steps_of_running_runs_once_per_backoff()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "LeaseFlow", runId, "manual", null, null);
        foreach (var key in new[] { "dead", "done", "live" })
        {
            await _store.TryAcquireStepLeaseAsync(runId, key, "a", Lease);
            await _store.RecordStepStartAsync(runId, key, "Work", null, null);
        }

        await _store.RecordStepCompleteAsync(runId, "done", "Succeeded", null, null);
        var closedRun = await StartLeaseRunAsync();
        await _store.TryAcquireStepLeaseAsync(closedRun, "dead", "a", Lease);
        await _store.CompleteRunAsync(closedRun, "Cancelled");
        await ExpireLeasesAsync(runId);
        await ExpireLeasesAsync(closedRun);
        await _store.RenewStepLeasesAsync([new StepLease(runId, "live", 1)], Lease);

        // Act
        var first = (await _store.LeaseExpiredStepLeasesAsync(Lease, 1000)).Where(e => e.RunId == runId || e.RunId == closedRun).ToList();
        var immediately = (await _store.LeaseExpiredStepLeasesAsync(Lease, 1000)).Where(e => e.RunId == runId).ToList();
        await AgeRedeliveriesAsync(runId);
        var afterBackoff = (await _store.LeaseExpiredStepLeasesAsync(Lease, 1000)).Where(e => e.RunId == runId).ToList();

        // Assert
        Assert.Equal(new ExpiredStepLease(runId, flowId, "dead"), Assert.Single(first));
        Assert.Empty(immediately);
        Assert.Equal("dead", Assert.Single(afterBackoff).StepKey);
    }

    [Fact]
    public async Task TryAcquireStepLeaseAsync_under_contention_hands_an_expired_lease_to_exactly_one_caller()
    {
        // Arrange
        var runId = await StartLeaseRunAsync();
        await _store.TryAcquireStepLeaseAsync(runId, "work", "dead", Lease);
        await _store.RecordStepStartAsync(runId, "work", "Work", null, null);
        await ExpireLeasesAsync(runId);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 16)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await _store.TryAcquireStepLeaseAsync(runId, "work", "r" + i, Lease);
            }))
            .ToArray();

        // Act
        gate.SetResult();
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(60));

        // Assert
        Assert.Equal(2, Assert.Single(results, r => r is not null)!.Fence);
    }

    [Fact]
    public async Task A_takeover_racing_the_old_attempts_completion_never_lets_both_win()
    {
        // Arrange — 20 rounds of "A's lease expired, A completes while B takes over".
        var outcomes = new List<(bool Completed, bool TookOver)>();
        for (var round = 0; round < 20; round++)
        {
            var runId = await StartLeaseRunAsync();
            var a = (await _store.TryAcquireStepLeaseAsync(runId, "work", "a", Lease))!;
            await _store.RecordStepStartAsync(runId, "work", "Work", null, null);
            await ExpireLeasesAsync(runId);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = Task.Run(async () => { await gate.Task; return await _store.TryRecordLeasedStepCompleteAsync(a, "Succeeded", null, null); });
            var takeover = Task.Run(async () => { await gate.Task; return await _store.TryAcquireStepLeaseAsync(runId, "work", "b", Lease); });

            // Act
            gate.SetResult();
            var completed = await completion.WaitAsync(TimeSpan.FromSeconds(60));
            var took = await takeover.WaitAsync(TimeSpan.FromSeconds(60));
            outcomes.Add((completed, took is not null));
        }

        // Assert — exactly one of the two wins every round: the step is never both completed and re-run.
        Assert.All(outcomes, o => Assert.True(o.Completed ^ o.TookOver, $"completed={o.Completed}, tookOver={o.TookOver}"));
    }
}
