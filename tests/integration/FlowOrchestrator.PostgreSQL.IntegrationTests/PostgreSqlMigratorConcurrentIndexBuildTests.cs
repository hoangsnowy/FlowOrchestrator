using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Pins that <see cref="PostgreSqlFlowOrchestratorMigrator"/> can run again on a database while a
/// run-search index build (<c>CREATE INDEX CONCURRENTLY</c>, started by an earlier start of this host or by
/// another replica) is still in flight, without waiting for the build, without stalling <c>flow_runs</c>
/// writers, and without deadlocking with it.
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
    private static async Task WaitUntilBuildIsParkedAsync(string cs)
    {
        const string ParkedSql = """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database()
                  AND query LIKE 'CREATE INDEX CONCURRENTLY%ix_flow_runs_run_search_trgm%'
                  AND wait_event_type = 'Lock' AND wait_event = 'virtualxid')
            """;
        using var timeout = new CancellationTokenSource(Budget);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(timeout.Token);
        while (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(ParkedSql, cancellationToken: timeout.Token)))
        {
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
