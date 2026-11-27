using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// <see cref="PostgreSqlFlowRunStore.TryTakeOverStaleIdempotencyKeyAsync"/> on a real PostgreSQL: the age is judged on
/// <c>NOW()</c>, a run with a ledger or step row keeps its key, the stale run is cancel-latched in
/// the same transaction, of 16 concurrent takeovers exactly one wins, and the per-run advisory gate that
/// excludes a concurrent first write blocks only the run being taken over.
/// </summary>
/// <remarks>
/// Registrations are aged by moving <c>created_at_utc</c> back relative to the database clock, never by waiting.
/// </remarks>
public sealed partial class PostgreSqlFlowRunStoreTests
{
    private static readonly TimeSpan ReservationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Registers <paramref name="key"/> for <paramref name="runId"/> and stamps it <paramref name="age"/> before the database clock.</summary>
    private async Task RegisterAgedKeyAsync(Guid flowId, string key, Guid runId, TimeSpan age)
    {
        Assert.True(await _store.TryRegisterIdempotencyKeyAsync(flowId, "webhook", key, runId));
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            UPDATE flow_idempotency_keys
            SET created_at_utc = NOW() - @Age
            WHERE flow_id = @FlowId AND run_id = @RunId
            """,
            new { FlowId = flowId, RunId = runId, Age = age });
    }

    [Theory]
    [InlineData(25, false)]
    [InlineData(35, true)]
    [InlineData(3_600, true)]
    public async Task TryTakeOverStaleIdempotencyKeyAsync_judges_age_on_the_database_clock(int ageSeconds, bool takenOver)
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var staleRunId = Guid.NewGuid();
        var newRunId = Guid.NewGuid();
        await RegisterAgedKeyAsync(flowId, "order-1", staleRunId, TimeSpan.FromSeconds(ageSeconds));

        // Act
        var owner = await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", "order-1", staleRunId, newRunId, ReservationTimeout);

        // Assert
        var expected = takenOver ? newRunId : staleRunId;
        Assert.Equal(expected, owner);
        Assert.Equal(expected, await _store.FindRunIdByIdempotencyKeyAsync(flowId, "webhook", "order-1"));
        Assert.Equal(takenOver, (await _store.GetRunControlAsync(staleRunId))?.CancelRequested ?? false);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("step")]
    public async Task TryTakeOverStaleIdempotencyKeyAsync_leaves_the_key_of_a_run_that_started_work(string evidence)
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var liveRunId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, "TakeoverFlow", liveRunId, "webhook", null, null);
        if (evidence == "ledger")
        {
            Assert.True(await _store.TryRecordDispatchAsync(liveRunId, "a"));
        }
        else
        {
            await _store.RecordStepStartAsync(liveRunId, "a", "Echo", null, null);
        }

        await RegisterAgedKeyAsync(flowId, "order-1", liveRunId, TimeSpan.FromMinutes(10));

        // Act
        var owner = await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", "order-1", liveRunId, Guid.NewGuid(), ReservationTimeout);

        // Assert
        Assert.Equal(liveRunId, owner);
        Assert.Equal(liveRunId, await _store.FindRunIdByIdempotencyKeyAsync(flowId, "webhook", "order-1"));
        Assert.Null(await _store.GetRunControlAsync(liveRunId));
    }

    [Fact]
    public async Task TryTakeOverStaleIdempotencyKeyAsync_answers_the_owner_or_null_without_writing()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var otherRunId = Guid.NewGuid();
        var staleRunId = Guid.NewGuid();
        await RegisterAgedKeyAsync(flowId, "order-1", otherRunId, TimeSpan.FromMinutes(10));

        // Act
        var ownedByOther = await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", "order-1", staleRunId, Guid.NewGuid(), ReservationTimeout);
        var missing = await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", "order-2", staleRunId, Guid.NewGuid(), ReservationTimeout);

        // Assert
        Assert.Equal(otherRunId, ownedByOther);
        Assert.Null(missing);
        Assert.Null(await _store.GetRunControlAsync(staleRunId));
    }

    [Fact]
    public async Task TryTakeOverStaleIdempotencyKeyAsync_matches_the_normalised_key_and_restamps_the_reservation()
    {
        // Arrange - a key longer than the column is stored as its digest.
        var flowId = Guid.NewGuid();
        var staleRunId = Guid.NewGuid();
        var newRunId = Guid.NewGuid();
        var longKey = new string('K', 300);
        await RegisterAgedKeyAsync(flowId, longKey, staleRunId, TimeSpan.FromMinutes(10));

        // Act
        var owner = await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, " WEBHOOK ", longKey.ToLowerInvariant() + " ", staleRunId, newRunId, ReservationTimeout);
        var immediateSecondTakeover = await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", longKey, newRunId, Guid.NewGuid(), ReservationTimeout);

        // Assert
        Assert.Equal(newRunId, owner);
        Assert.Equal(newRunId, immediateSecondTakeover);
        Assert.Equal(newRunId, await _store.FindRunIdByIdempotencyKeyAsync(flowId, "webhook", longKey));
    }

    [Fact]
    public async Task TryTakeOverStaleIdempotencyKeyAsync_cancel_latches_the_stale_run_keeping_an_earlier_cancel()
    {
        // Arrange - one stale run without a control record, one with a recorded operator cancel.
        var flowId = Guid.NewGuid();
        var bareRunId = Guid.NewGuid();
        var cancelledRunId = Guid.NewGuid();
        var newRunId = Guid.NewGuid();
        await RegisterAgedKeyAsync(flowId, "order-1", bareRunId, TimeSpan.FromMinutes(1));
        await RegisterAgedKeyAsync(flowId, "order-2", cancelledRunId, TimeSpan.FromMinutes(1));
        await _store.ConfigureRunAsync(cancelledRunId, flowId, "webhook", "order-2", null);
        Assert.True(await _store.RequestCancelAsync(cancelledRunId, "operator"));

        // Act
        await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", "order-1", bareRunId, newRunId, ReservationTimeout);
        await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", "order-2", cancelledRunId, Guid.NewGuid(), ReservationTimeout);

        // Assert
        var created = await _store.GetRunControlAsync(bareRunId);
        Assert.NotNull(created);
        Assert.True(created.CancelRequested);
        Assert.Equal(IdempotencyKeyStorage.TakenOverCancelReason(newRunId), created.CancelReason);
        Assert.Equal(flowId, created.FlowId);
        Assert.Null(created.TimeoutAtUtc);
        var kept = await _store.GetRunControlAsync(cancelledRunId);
        Assert.NotNull(kept);
        Assert.True(kept.CancelRequested);
        Assert.Equal("operator", kept.CancelReason);
    }

    [Fact]
    public async Task TryTakeOverStaleIdempotencyKeyAsync_under_contention_has_exactly_one_winner_and_never_throws()
    {
        // Arrange
        const int contenders = 16;
        var flowId = Guid.NewGuid();

        for (var round = 0; round < Rounds; round++)
        {
            var key = $"order-{round}";
            var staleRunId = Guid.NewGuid();
            await RegisterAgedKeyAsync(flowId, key, staleRunId, TimeSpan.FromMinutes(1));
            var candidates = Enumerable.Range(0, contenders).Select(_ => Guid.NewGuid()).ToArray();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = candidates.Select(candidate => Task.Run(async () =>
            {
                await gate.Task;
                return await _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", key, staleRunId, candidate, ReservationTimeout);
            })).ToArray();

            // Act
            gate.SetResult();
            var owners = await Task.WhenAll(attempts);

            // Assert
            var winner = await _store.FindRunIdByIdempotencyKeyAsync(flowId, "webhook", key);
            Assert.NotNull(winner);
            Assert.Single(Enumerable.Range(0, contenders), i => owners[i] == candidates[i]);
            Assert.All(owners, owner => Assert.Equal(winner, owner));
            Assert.True((await _store.GetRunControlAsync(staleRunId))?.CancelRequested);
        }
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("step")]
    public async Task TryTakeOverStaleIdempotencyKeyAsync_waits_for_an_uncommitted_first_write_and_then_refuses(string firstWrite)
    {
        // Arrange - the original trigger is only slow: its first ledger (or step) row is being written under the
        // run gate, not yet committed. READ COMMITTED does not block a read on an uncommitted insert, so only the
        // gate keeps the takeover from deciding on a snapshot without the row.
        var flowId = Guid.NewGuid();
        var slowRunId = Guid.NewGuid();
        await RegisterAgedKeyAsync(flowId, "order-1", slowRunId, TimeSpan.FromMinutes(1));
        await using var writer = new NpgsqlConnection(_connectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        if (firstWrite == "ledger")
        {
            await writer.ExecuteAsync(PostgreSqlFlowRunStore.TryRecordDispatchSql, new { RunId = slowRunId, StepKey = "a" }, tx);
        }
        else
        {
            await writer.ExecuteAsync($"SELECT 1 FROM {PostgreSqlFlowRunStore.RunGateSharedFrom}", new { RunId = slowRunId }, tx);
            await writer.ExecuteAsync(
                "INSERT INTO flow_steps (run_id, step_key, step_type, status, started_at) VALUES (@RunId, 'a', 'Echo', 'Running', NOW())",
                new { RunId = slowRunId },
                tx);
        }

        // Act
        var takeover = _store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", "order-1", slowRunId, Guid.NewGuid(), ReservationTimeout);
        var finishedWhileUncommitted = await Task.WhenAny(takeover, Task.Delay(TimeSpan.FromMilliseconds(250))) == takeover;
        await tx.CommitAsync();
        var owner = await takeover.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.False(finishedWhileUncommitted);
        Assert.Equal(slowRunId, owner);
        Assert.Null(await _store.GetRunControlAsync(slowRunId));
    }

    [Fact]
    public async Task A_held_takeover_gate_blocks_only_its_own_run()
    {
        // Arrange - a takeover of run A holds A's gate (exclusive) in an open transaction.
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        await using var takeover = new NpgsqlConnection(_connectionString);
        await takeover.OpenAsync();
        await using var tx = await takeover.BeginTransactionAsync();
        await takeover.ExecuteAsync(PostgreSqlFlowRunStore.RunGateExclusiveSql, new { RunId = runA }, tx);

        // Act
        var otherRun = await _store.TryRecordDispatchAsync(runB, "a").WaitAsync(TimeSpan.FromSeconds(30));
        await _store.RecordStepStartAsync(runB, "b", "Echo", null, null).WaitAsync(TimeSpan.FromSeconds(30));
        var sameRun = _store.TryRecordDispatchAsync(runA, "a");
        var sameRunFinishedWhileHeld = await Task.WhenAny(sameRun, Task.Delay(TimeSpan.FromMilliseconds(250))) == sameRun;
        await tx.CommitAsync();
        var sameRunRecorded = await sameRun.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert - run B never waited; run A's first dispatch waited for the takeover, then landed.
        Assert.True(otherRun);
        Assert.Equal(StepStatus.Running, (await _store.GetStepStatusesAsync(runB))["b"]);
        Assert.False(sameRunFinishedWhileHeld);
        Assert.True(sameRunRecorded);
    }

    [Fact]
    public async Task Writers_of_one_run_do_not_wait_on_each_other_through_the_gate()
    {
        // Arrange - an open transaction holds run A's gate shared, as a dispatch in flight does.
        var runA = Guid.NewGuid();
        await using var writer = new NpgsqlConnection(_connectionString);
        await writer.OpenAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await writer.ExecuteAsync(PostgreSqlFlowRunStore.TryRecordDispatchSql, new { RunId = runA, StepKey = "a" }, tx);

        // Act - other dispatches and a step start of the same run (shared with shared).
        var second = await _store.TryRecordDispatchAsync(runA, "b").WaitAsync(TimeSpan.FromSeconds(30));
        await _store.RecordStepStartAsync(runA, "c", "Echo", null, null).WaitAsync(TimeSpan.FromSeconds(30));
        await _store.RecordSkippedStepAsync(runA, "d", "Echo", "When condition evaluated to false.").WaitAsync(TimeSpan.FromSeconds(30));
        await tx.CommitAsync();

        // Assert
        Assert.True(second);
        var statuses = await _store.GetStepStatusesAsync(runA);
        Assert.Equal(StepStatus.Running, statuses["c"]);
        Assert.Equal(StepStatus.Skipped, statuses["d"]);
        Assert.Equal(["a", "b"], (await _store.GetDispatchedStepKeysAsync(runA)).Order(StringComparer.Ordinal));
    }
}
