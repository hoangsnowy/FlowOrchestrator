using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// <see cref="SqlFlowRunStore.TryTakeOverStaleIdempotencyKeyAsync"/> on a real SQL Server: the age is judged on
/// <c>SYSDATETIMEOFFSET()</c>, a run with a ledger or step row keeps its key, the stale run is cancel-latched in
/// the same transaction, and of 16 concurrent takeovers exactly one wins.
/// </summary>
/// <remarks>
/// Registrations are aged by moving <c>CreatedAtUtc</c> back relative to the database clock, never by waiting.
/// </remarks>
public sealed partial class SqlFlowRunStoreTests
{
    private static readonly TimeSpan ReservationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Registers <paramref name="key"/> for <paramref name="runId"/> and stamps it <paramref name="age"/> before the database clock.</summary>
    private async Task RegisterAgedKeyAsync(Guid flowId, string key, Guid runId, TimeSpan age)
    {
        Assert.True(await _store.TryRegisterIdempotencyKeyAsync(flowId, "webhook", key, runId));
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            UPDATE FlowIdempotencyKeys
            SET CreatedAtUtc = DATEADD(MILLISECOND, -@AgeMs, SYSDATETIMEOFFSET())
            WHERE FlowId = @FlowId AND RunId = @RunId
            """,
            new { FlowId = flowId, RunId = runId, AgeMs = (int)age.TotalMilliseconds });
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryTakeOverStaleIdempotencyKeyAsync_waits_for_an_uncommitted_first_dispatch_and_then_refuses(bool readCommittedSnapshot)
    {
        // Arrange - the original trigger is only slow: its first ledger row is being written, not yet committed.
        // READ_COMMITTED_SNAPSHOT (the Azure SQL default) makes a plain read skip the uncommitted row instead of
        // blocking on it, so only the HOLDLOCK probe keeps the takeover from deciding without it.
        var connectionString = readCommittedSnapshot ? await CreateSnapshotDatabaseAsync() : _connectionString;
        var store = new SqlFlowRunStore(connectionString);
        var flowId = Guid.NewGuid();
        var slowRunId = Guid.NewGuid();
        Assert.True(await store.TryRegisterIdempotencyKeyAsync(flowId, "webhook", "order-1", slowRunId));
        await using var writer = new SqlConnection(connectionString);
        await writer.OpenAsync();
        await writer.ExecuteAsync(
            "UPDATE FlowIdempotencyKeys SET CreatedAtUtc = DATEADD(MINUTE, -1, SYSDATETIMEOFFSET()) WHERE FlowId = @FlowId",
            new { FlowId = flowId });
        await using var tx = (SqlTransaction)await writer.BeginTransactionAsync();
        await writer.ExecuteAsync(
            "INSERT INTO FlowStepDispatches (RunId, StepKey, DispatchedAt) VALUES (@RunId, 'a', SYSDATETIMEOFFSET())",
            new { RunId = slowRunId },
            tx);

        // Act - the takeover must block on the key-range lock instead of deciding on a snapshot without the row.
        var takeover = store.TryTakeOverStaleIdempotencyKeyAsync(flowId, "webhook", "order-1", slowRunId, Guid.NewGuid(), ReservationTimeout);
        var finishedWhileUncommitted = await Task.WhenAny(takeover, Task.Delay(TimeSpan.FromMilliseconds(250))) == takeover;
        await tx.CommitAsync();
        var owner = await takeover;

        // Assert
        Assert.False(finishedWhileUncommitted);
        Assert.Equal(slowRunId, owner);
        Assert.Null(await store.GetRunControlAsync(slowRunId));
    }

    /// <summary>
    /// Creates a FlowOrchestrator database with <c>READ_COMMITTED_SNAPSHOT ON</c> in the fixture's server and
    /// returns its connection string.
    /// </summary>
    private async Task<string> CreateSnapshotDatabaseAsync()
    {
        var database = $"fo_rcsi_{Guid.NewGuid():N}";
        await using (var master = new SqlConnection(_connectionString))
        {
            await master.ExecuteAsync($"CREATE DATABASE [{database}]; ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT ON;");
        }

        var connectionString = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = database }.ConnectionString;
        var migrator = new FlowOrchestratorSqlMigrator(
            connectionString,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowOrchestratorSqlMigrator>.Instance,
            runSearchIndexEnabled: false);
        await migrator.StartAsync(CancellationToken.None);
        return connectionString;
    }
}
