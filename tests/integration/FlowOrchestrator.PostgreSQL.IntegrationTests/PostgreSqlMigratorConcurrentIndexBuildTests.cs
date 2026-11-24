using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Pins that <see cref="PostgreSqlFlowOrchestratorMigrator"/> can start beside running hosts (a rolling
/// deployment, a restart): on an up-to-date schema it requests no lock on any table, so it neither waits for
/// nor stalls writers, autovacuum or a run-search index build (<c>CREATE INDEX CONCURRENTLY</c>) and cannot
/// deadlock with one; it leaves another host's in-flight build alone; and it still repairs a build that was
/// genuinely interrupted.
/// </summary>
/// <remarks>
/// Regression for the intermittent <c>40P01: deadlock detected</c> in
/// <see cref="PostgreSqlFlowEventsRemovalTests"/>: the schema batch requested SHARE / ACCESS EXCLUSIVE on
/// <c>flow_runs</c> for no-op <c>IF NOT EXISTS</c> statements, queued behind the build's
/// SHARE UPDATE EXCLUSIVE lock, and the build then waited for the batch's snapshot.
/// </remarks>
public sealed class PostgreSqlMigratorConcurrentIndexBuildTests : IClassFixture<PostgreSqlFixture>
{
    // Generous: the healthy path finishes in milliseconds; only a regression ever spends this budget.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly string _serverConnectionString;

    /// <summary>Uses the class's container only as a server; the test migrates a database of its own.</summary>
    /// <param name="fixture">The per-class PostgreSQL container.</param>
    public PostgreSqlMigratorConcurrentIndexBuildTests(PostgreSqlFixture fixture)
    {
        _serverConnectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task Migration_rerun_during_an_in_flight_index_build_neither_waits_nor_blocks_writers_nor_kills_the_build()
    {
        // Arrange — an up-to-date schema whose run-search index is missing, so the next start builds it.
        var cs = await CreateDatabaseAsync();
        using (var initial = new PostgreSqlFlowOrchestratorMigrator(cs, new CapturingLogger()))
        {
            await initial.StartAsync(CancellationToken.None);
            await initial.BackgroundIndexBuild;
        }

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.ExecuteAsync("DROP INDEX ix_flow_runs_run_search_trgm");
        }

        // An open REPEATABLE READ snapshot parks the CONCURRENTLY build in its wait for older snapshots,
        // where it keeps holding SHARE UPDATE EXCLUSIVE on flow_runs until the snapshot closes.
        await using var snapshotHolder = new NpgsqlConnection(cs);
        await snapshotHolder.OpenAsync();
        await using var snapshotTx = await snapshotHolder.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await snapshotHolder.ExecuteScalarAsync<int>("SELECT 1", transaction: snapshotTx);

        var buildingLog = new CapturingLogger();
        using var building = new PostgreSqlFlowOrchestratorMigrator(cs, buildingLog);
        await building.StartAsync(CancellationToken.None);
        await WaitUntilBuildIsParkedAsync(cs);

        using var restarted = new PostgreSqlFlowOrchestratorMigrator(cs, new CapturingLogger());
        var store = new PostgreSqlFlowRunStore(cs);
        string? rerunFailure, writeFailure;
        bool buildStillRunning;

        // Act — the restarted host's migration and a running host's write, both while the build is parked.
        try
        {
            var rerun = OutcomeWithinBudgetAsync(restarted.StartAsync(CancellationToken.None));
            var write = OutcomeWithinBudgetAsync(
                store.StartRunAsync(Guid.NewGuid(), "ConcurrentBuildFlow", Guid.NewGuid(), "manual", null, null));
            rerunFailure = await rerun;
            writeFailure = await write;
            buildStillRunning = !building.BackgroundIndexBuild.IsCompleted;
        }
        finally
        {
            await snapshotTx.CommitAsync();
        }

        await building.BackgroundIndexBuild.WaitAsync(Budget);
        await restarted.BackgroundIndexBuild.WaitAsync(Budget);

        // Assert
        Assert.True(rerunFailure is null, $"The re-run migration did not complete beside the in-flight index build: {rerunFailure}");
        Assert.True(writeFailure is null, $"A flow_runs write did not complete during the re-run migration: {writeFailure}");
        Assert.True(buildStillRunning, "The build was not in flight during the re-run, so the test proved nothing.");
        Assert.DoesNotContain(buildingLog.Entries, e => e.EventId == 7213);
    }

    [Fact]
    public async Task Host_starting_during_another_hosts_index_build_leaves_that_build_alone()
    {
        // Arrange — host A's build is in flight (parked on an open snapshot), so the index is INVALID.
        var cs = await CreateMigratedDatabaseWithoutSearchIndexAsync();
        await using var snapshotHolder = await OpenSnapshotHolderAsync(cs);
        var hostALog = new CapturingLogger();
        using var hostA = new PostgreSqlFlowOrchestratorMigrator(cs, hostALog);
        await hostA.StartAsync(CancellationToken.None);
        var buildingPid = await WaitUntilBuildIsParkedAsync(cs);
        var indexBeingBuilt = await SearchIndexOidAsync(cs);
        var hostBLog = new CapturingLogger();
        using var hostB = new PostgreSqlFlowOrchestratorMigrator(cs, hostBLog);

        // Act — host B starts, its background build decides what to do, then host A's build may finish.
        await hostB.StartAsync(CancellationToken.None);
        await WaitUntilBuildDecidedAsync(cs, hostB, buildingPid);
        await snapshotHolder.DisposeAsync();
        await hostA.BackgroundIndexBuild.WaitAsync(Budget);
        await hostB.BackgroundIndexBuild.WaitAsync(Budget);

        // Assert — the index host A built is the one that stays; host B neither dropped nor rebuilt it.
        Assert.NotNull(indexBeingBuilt);
        Assert.Equal(indexBeingBuilt, await SearchIndexOidAsync(cs));
        Assert.True(await SearchIndexValidAsync(cs));
        Assert.DoesNotContain(hostBLog.Entries, e => e.EventId == 7211);
        Assert.Contains(hostBLog.Entries, e => e.EventId == 7215);
        Assert.DoesNotContain(hostALog.Entries, e => e.EventId == 7213);
    }

    [Fact]
    public async Task Build_interrupted_by_host_shutdown_is_repaired_by_the_next_start()
    {
        // Arrange — host A's build is in flight, then host A stops: PostgreSQL leaves the index INVALID.
        var cs = await CreateMigratedDatabaseWithoutSearchIndexAsync();
        await using (await OpenSnapshotHolderAsync(cs))
        {
            using var hostA = new PostgreSqlFlowOrchestratorMigrator(cs, new CapturingLogger());
            await hostA.StartAsync(CancellationToken.None);
            await WaitUntilBuildIsParkedAsync(cs);
            await hostA.StopAsync(CancellationToken.None);
            await hostA.BackgroundIndexBuild.WaitAsync(Budget);
        }

        var leftoverValid = await SearchIndexValidAsync(cs);
        var restartLog = new CapturingLogger();
        using var restarted = new PostgreSqlFlowOrchestratorMigrator(cs, restartLog);

        // Act
        await restarted.StartAsync(CancellationToken.None);
        await restarted.BackgroundIndexBuild.WaitAsync(Budget);

        // Assert
        Assert.False(leftoverValid);
        Assert.True(await SearchIndexValidAsync(cs));
        Assert.Contains(restartLog.Entries, e => e.EventId == 7212);
        Assert.DoesNotContain(restartLog.Entries, e => e.EventId == 7215);
    }

    [Fact]
    public async Task Migration_rerun_on_an_up_to_date_schema_requests_no_lock_a_running_host_could_be_holding()
    {
        // Arrange — a fully migrated database, then one open transaction that holds, on EVERY table, what a
        // running cluster holds: ROW EXCLUSIVE (an in-flight write) and SHARE UPDATE EXCLUSIVE (autovacuum,
        // ANALYZE, an index build). Any DDL lock the re-run requests on any table conflicts with one of them.
        var cs = await CreateDatabaseAsync();
        using (var initial = new PostgreSqlFlowOrchestratorMigrator(cs, new CapturingLogger()))
        {
            await initial.StartAsync(CancellationToken.None);
            await initial.BackgroundIndexBuild;
        }

        var store = new PostgreSqlFlowRunStore(cs);
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "RerunLockFlow", runId, "manual", null, null);
        await using var holder = new NpgsqlConnection(cs);
        await holder.OpenAsync();
        var tables = string.Join(", ", await holder.QueryAsync<string>(
            "SELECT quote_ident(tablename) FROM pg_tables WHERE schemaname = current_schema() ORDER BY tablename"));
        await holder.ExecuteAsync(
            $"BEGIN; LOCK TABLE {tables} IN ROW EXCLUSIVE MODE; LOCK TABLE {tables} IN SHARE UPDATE EXCLUSIVE MODE;");
        var rerunLog = new CapturingLogger();
        using var restarted = new PostgreSqlFlowOrchestratorMigrator(cs, rerunLog);
        string? rerunFailure, writeFailure;

        // Act — the restarted host's migration and a running host's writes, both beside the holder.
        try
        {
            var rerun = OutcomeWithinBudgetAsync(restarted.StartAsync(CancellationToken.None));
            var write = OutcomeWithinBudgetAsync(Task.Run(async () =>
            {
                await store.StartRunAsync(Guid.NewGuid(), "RerunLockFlow", Guid.NewGuid(), "manual", null, null);
                await store.RecordStepStartAsync(runId, "a", "Work", null, null);
                await store.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
            }));
            rerunFailure = await rerun;
            writeFailure = await write;
        }
        finally
        {
            await holder.ExecuteAsync("ROLLBACK");
        }

        await restarted.BackgroundIndexBuild.WaitAsync(Budget);

        // Assert — the trigram step swallows its own failures as warnings, so a warning is a failure too.
        Assert.True(rerunFailure is null, $"The re-run migration waited on a lock a running host holds: {rerunFailure}");
        Assert.True(writeFailure is null, $"A write did not complete during the re-run migration: {writeFailure}");
        Assert.DoesNotContain(rerunLog.Entries, e => e.Level >= LogLevel.Warning);
        Assert.NotEmpty(tables);
    }

    /// <summary>A migrated database whose run-search index is missing, so the next start builds it.</summary>
    private async Task<string> CreateMigratedDatabaseWithoutSearchIndexAsync()
    {
        var cs = await CreateDatabaseAsync();
        using (var initial = new PostgreSqlFlowOrchestratorMigrator(cs, new CapturingLogger()))
        {
            await initial.StartAsync(CancellationToken.None);
            await initial.BackgroundIndexBuild;
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.ExecuteAsync("DROP INDEX ix_flow_runs_run_search_trgm");
        return cs;
    }

    /// <summary>
    /// Opens a REPEATABLE READ transaction with a live snapshot. A CONCURRENTLY build started after it parks in
    /// its wait for older snapshots, holding SHARE UPDATE EXCLUSIVE on flow_runs, until this is disposed.
    /// </summary>
    private static async Task<NpgsqlConnection> OpenSnapshotHolderAsync(string cs)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("BEGIN ISOLATION LEVEL REPEATABLE READ; SELECT 1;");
        return conn;
    }

    private static async Task<long?> SearchIndexOidAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        return await conn.ExecuteScalarAsync<long?>("SELECT to_regclass('ix_flow_runs_run_search_trgm')::oid::bigint");
    }

    private static async Task<bool?> SearchIndexValidAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        return await conn.ExecuteScalarAsync<bool?>(PostgreSqlFlowOrchestratorMigrator.RunSearchIndexStateSql);
    }

    /// <summary>
    /// Waits until <paramref name="host"/>'s background build has made its decision: it finished, or it is
    /// blocked on a lock in a CONCURRENTLY statement of its own (the drop-and-rebuild path).
    /// </summary>
    private static async Task WaitUntilBuildDecidedAsync(string cs, PostgreSqlFlowOrchestratorMigrator host, int buildingPid)
    {
        const string BlockedSql = """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> @buildingPid
                  AND query LIKE '% INDEX CONCURRENTLY%ix_flow_runs_run_search_trgm%'
                  AND wait_event_type = 'Lock')
            """;
        using var timeout = new CancellationTokenSource(Budget);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(timeout.Token);
        while (!host.BackgroundIndexBuild.IsCompleted
               && !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(BlockedSql, new { buildingPid }, cancellationToken: timeout.Token)))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var name = "fo_cic_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(_serverConnectionString))
        {
            await conn.ExecuteAsync($"CREATE DATABASE {name}");
        }

        return new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = name, IncludeErrorDetail = true }.ConnectionString;
    }

    /// <summary>
    /// Waits until the background build is blocked on another transaction's virtual xid, i.e. it holds its
    /// table lock and cannot finish. The other backend's lock state is only observable through the catalog,
    /// so this polls it — bounded by <see cref="Budget"/>, with a precise failure message.
    /// </summary>
    /// <returns>The building backend's pid.</returns>
    private static async Task<int> WaitUntilBuildIsParkedAsync(string cs)
    {
        const string ParkedSql = """
            SELECT pid FROM pg_stat_activity
            WHERE datname = current_database()
              AND query LIKE 'CREATE INDEX CONCURRENTLY%ix_flow_runs_run_search_trgm%'
              AND wait_event_type = 'Lock' AND wait_event = 'virtualxid'
            LIMIT 1
            """;
        using var timeout = new CancellationTokenSource(Budget);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(timeout.Token);
        while (true)
        {
            var pid = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(ParkedSql, cancellationToken: timeout.Token));
            if (pid is { } parked)
                return parked;
            await Task.Delay(25, timeout.Token);
        }
    }

    /// <summary>Returns <see langword="null"/> when <paramref name="task"/> succeeds within <see cref="Budget"/>, otherwise why not.</summary>
    private static async Task<string?> OutcomeWithinBudgetAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Budget);
            return null;
        }
        catch (Exception ex)
        {
            // Timed out here, or failed on its own (Npgsql's 30 s command timeout, 40P01 deadlock).
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private sealed class CapturingLogger : ILogger<PostgreSqlFlowOrchestratorMigrator>
    {
        public ConcurrentQueue<(int EventId, LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((eventId.Id, logLevel, formatter(state, exception)));
    }
}
