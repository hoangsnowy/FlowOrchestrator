using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// The start-up migration of a host that starts beside running hosts (a rolling deployment, a restart):
/// on an up-to-date schema it must request no table lock, so it neither queues behind the running hosts'
/// writes nor makes their later writes queue behind it. And guarding the DDL must not change the schema.
/// </summary>
/// <remarks>
/// Each test migrates its own database inside the shared container, so the class fixture's pre-migrated
/// database is never locked or compared.
/// </remarks>
public sealed class PostgreSqlMigratorStartupLockTests : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly PostgreSqlFixture _fixture;

    /// <summary>Binds the tests to the shared container.</summary>
    /// <param name="fixture">The database container fixture.</param>
    public PostgreSqlMigratorStartupLockTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_is_idempotent_and_produces_the_schema_of_the_unguarded_1_34_7_sql()
    {
        // Arrange — one database migrated by the SQL 1.34.7 shipped, one fresh database for the current migrator.
        var baselineCs = await CreateDatabaseAsync();
        await using (var conn = new NpgsqlConnection(baselineCs))
        {
            await conn.ExecuteAsync(PostgreSqlMigrationBaseline.MigrationSql);
            await conn.ExecuteAsync(PostgreSqlMigrationBaseline.TrigramSql);
        }

        var expected = await SchemaAsync(baselineCs);
        var cs = await CreateDatabaseAsync();
        var log = new CapturingLogger();

        // Act — a first start, then a second start on the now up-to-date schema.
        await new PostgreSqlFlowOrchestratorMigrator(cs, log).StartAsync(CancellationToken.None);
        var afterFirstStart = await SchemaAsync(cs);
        await new PostgreSqlFlowOrchestratorMigrator(cs, log).StartAsync(CancellationToken.None);
        var afterSecondStart = await SchemaAsync(cs);

        // Assert — the trigram step swallows its own failures as warnings, so a warning is a failure too.
        Assert.Contains(expected, l => l.Contains("ix_flow_steps_output_json_trgm", StringComparison.Ordinal));
        Assert.Contains(expected, l => l.Contains("flow_steps.evaluation_trace_json", StringComparison.Ordinal));
        Assert.Equal(expected, afterFirstStart);
        Assert.Equal(expected, afterSecondStart);
        Assert.True(log.Warnings.Length == 0, "Migration warnings: " + string.Join(" | ", log.Warnings));
    }

    [Fact]
    public async Task Second_start_on_an_up_to_date_schema_completes_while_another_session_holds_write_locks()
    {
        // Arrange — a migrated database, then one open transaction that holds, on EVERY table, what a running
        // cluster holds: ROW EXCLUSIVE (an in-flight write) and SHARE UPDATE EXCLUSIVE (autovacuum, ANALYZE).
        // CREATE INDEX (SHARE) conflicts with both; ADD COLUMN (ACCESS EXCLUSIVE) conflicts with everything.
        var cs = await CreateDatabaseAsync();
        await new PostgreSqlFlowOrchestratorMigrator(cs, new CapturingLogger()).StartAsync(CancellationToken.None);

        var store = new PostgreSqlFlowRunStore(cs);
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "StartupLockFlow", runId, "manual", null, null);

        await using var holder = new NpgsqlConnection(cs);
        await holder.OpenAsync();
        var tables = string.Join(", ", await holder.QueryAsync<string>(
            "SELECT quote_ident(tablename) FROM pg_tables WHERE schemaname = current_schema() ORDER BY tablename"));
        await holder.ExecuteAsync(
            $"BEGIN; LOCK TABLE {tables} IN ROW EXCLUSIVE MODE; LOCK TABLE {tables} IN SHARE UPDATE EXCLUSIVE MODE;");

        var rerunLog = new CapturingLogger();
        var restarted = new PostgreSqlFlowOrchestratorMigrator(cs, rerunLog);
        string? rerunFailure, writeFailure, lockWaits = null;

        // Act — the starting host's migration and a running host's writes, both beside the holder.
        try
        {
            var rerun = restarted.StartAsync(CancellationToken.None);
            var rerunOutcome = OutcomeWithinBudgetAsync(rerun);
            var writeOutcome = OutcomeWithinBudgetAsync(Task.Run(async () =>
            {
                await store.StartRunAsync(Guid.NewGuid(), "StartupLockFlow", Guid.NewGuid(), "manual", null, null);
                await store.RecordStepStartAsync(runId, "a", "Work", null, null);
                await store.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
            }));

            // Diagnostics only: the guarded start takes milliseconds. If it is still running after a few
            // seconds, record who waits on what while the state is live, so a failure names the statement.
            if (await Task.WhenAny(rerun, Task.Delay(TimeSpan.FromSeconds(5))) != rerun)
                lockWaits = await DescribeLockWaitsAsync(cs);

            rerunFailure = await rerunOutcome;
            writeFailure = await writeOutcome;
        }
        finally
        {
            await holder.ExecuteAsync("ROLLBACK");
        }

        // Assert
        Assert.True(rerunFailure is null, $"The second start waited on a lock another session holds: {rerunFailure}\nLock waits: {lockWaits}");
        Assert.True(writeFailure is null, $"A write did not complete during the second start: {writeFailure}");
        Assert.True(rerunLog.Warnings.Length == 0, "Second start warnings: " + string.Join(" | ", rerunLog.Warnings));
        Assert.NotEmpty(tables);
    }

    /// <summary>Creates an empty database in the shared container and returns its connection string.</summary>
    private async Task<string> CreateDatabaseAsync()
    {
        var name = "fo_startup_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {name}");
        }

        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>
    /// Every index (definition and validity) and every column (type, length, nullability, default) of the
    /// database's schema, one line each, in a stable order.
    /// </summary>
    private static async Task<string[]> SchemaAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        var indexes = await conn.QueryAsync<string>("""
            SELECT format('index %s.%s valid=%s :: %s', i.tablename, i.indexname, x.indisvalid, i.indexdef)
            FROM pg_indexes AS i
            JOIN pg_namespace AS n ON n.nspname = i.schemaname
            JOIN pg_class AS c ON c.relname = i.indexname AND c.relnamespace = n.oid
            JOIN pg_index AS x ON x.indexrelid = c.oid
            WHERE i.schemaname = current_schema()
            ORDER BY i.tablename, i.indexname
            """);
        var columns = await conn.QueryAsync<string>("""
            SELECT format('column %s.%s #%s %s(%s) nullable=%s default=%s',
                          table_name, column_name, ordinal_position, data_type,
                          coalesce(character_maximum_length::text, ''), is_nullable, coalesce(column_default, ''))
            FROM information_schema.columns
            WHERE table_schema = current_schema()
            ORDER BY table_name, ordinal_position
            """);
        return indexes.Concat(columns).ToArray();
    }

    /// <summary>Awaits <paramref name="task"/> within <see cref="Budget"/>; <see langword="null"/> when it succeeded.</summary>
    private static async Task<string?> OutcomeWithinBudgetAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Budget);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Every backend of the database with its wait and the pids blocking it, and every lock request not yet
    /// granted, as one line each: what a failing run needs to name the statement and the lock it waited on.
    /// </summary>
    private static async Task<string> DescribeLockWaitsAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        var activity = await conn.QueryAsync<string>("""
            SELECT format('pid %s %s wait=%s/%s blocked_by=%s :: %s',
                          a.pid, a.state, a.wait_event_type, a.wait_event,
                          pg_blocking_pids(a.pid), left(regexp_replace(a.query, '\s+', ' ', 'g'), 160))
            FROM pg_stat_activity AS a
            WHERE a.datname = current_database() AND a.pid <> pg_backend_pid()
            """);
        var waiting = await conn.QueryAsync<string>("""
            SELECT format('pid %s waits %s on %s %s', l.pid, l.mode, l.locktype,
                          coalesce(l.relation::regclass::text, l.virtualxid, l.transactionid::text, ''))
            FROM pg_locks AS l
            WHERE NOT l.granted
            """);
        return string.Join(" || ", activity.Concat(waiting));
    }

    /// <summary>Records every log entry the migrator writes, so a swallowed failure can be asserted on.</summary>
    private sealed class CapturingLogger : ILogger<PostgreSqlFlowOrchestratorMigrator>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public string[] Warnings => _entries
            .Where(e => e.Level >= LogLevel.Warning)
            .Select(e => $"{e.Level} {e.Message}")
            .ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " :: " + exception.Message)));
    }
}
