using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// Pins that several hosts can start <see cref="FlowOrchestratorSqlMigrator"/> against one database at the
/// same time (a rolling deployment, a scale-out): the run-search indexes are built once, no host's start
/// fails or reports a false build failure, and a start beside an in-flight online build neither waits for
/// it nor stalls writers.
/// </summary>
/// <remarks>
/// Before the fix both racing hosts passed the build's <c>IF NOT EXISTS</c> guard and the loser failed with
/// error 1913: on the offline path (editions without online index builds) its start threw, on the online
/// path it logged warning 7206.
/// </remarks>
public sealed class SqlMigratorConcurrentStartTests : IClassFixture<SqlServerFixture>
{
    // Generous: the healthy path finishes in about a second; only a regression ever spends this budget.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    // Enough rows that one build takes long enough for two starts to overlap.
    private const int SeedRuns = 300_000;

    private readonly string _serverConnectionString;

    /// <summary>Uses the class's container only as a server; each test migrates a database of its own.</summary>
    /// <param name="fixture">The per-class SQL Server container.</param>
    public SqlMigratorConcurrentStartTests(SqlServerFixture fixture)
    {
        _serverConnectionString = fixture.ConnectionString;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hosts_starting_together_build_the_run_search_indexes_once_and_all_start(bool online)
    {
        // Arrange
        var cs = await CreateSeededDatabaseWithoutSearchIndexesAsync();
        var failures = new List<string>();
        var falseBuildFailures = 0;

        // Act — three rounds of two hosts released at the same instant.
        for (var round = 0; round < 3; round++)
        {
            await DropSearchIndexesAsync(cs);
            var logs = new[] { new CapturingLogger(), new CapturingLogger() };
            var hosts = logs.Select(log => new FlowOrchestratorSqlMigrator(cs, log) { OnlineIndexBuildOverride = online }).ToArray();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var starts = hosts.Select(host => Task.Run(async () =>
            {
                await gate.Task;
                await host.StartAsync(CancellationToken.None);
                await host.BackgroundIndexBuild;
            })).ToArray();
            gate.SetResult();
            foreach (var start in starts)
            {
                try
                {
                    await start.WaitAsync(Budget);
                }
                catch (Exception ex)
                {
                    failures.Add($"round {round}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            falseBuildFailures += logs.Sum(log => log.Entries.Count(e => e.EventId == 7206));
            foreach (var host in hosts) host.Dispose();
        }

        // Assert
        Assert.True(failures.Count == 0, "A host's start failed: " + string.Join(" | ", failures));
        Assert.Equal(0, falseBuildFailures);
        Assert.Equal(2, await SearchIndexCountAsync(cs));
    }

    [Fact]
    public async Task Host_starting_beside_an_in_flight_online_build_neither_waits_nor_stalls_writers_nor_builds_again()
    {
        // Arrange — host A's online build parked in its prepare phase behind an open write transaction.
        var cs = await CreateSeededDatabaseWithoutSearchIndexesAsync(seedRuns: 1);
        await using var holder = new SqlConnection(cs);
        await holder.OpenAsync();
        var seededRun = await holder.ExecuteScalarAsync<Guid>("SELECT TOP (1) Id FROM FlowRuns");
        var holderTx = holder.BeginTransaction();
        await holder.ExecuteAsync("UPDATE FlowRuns SET Status = Status WHERE Id = @seededRun", new { seededRun }, holderTx);
        var hostALog = new CapturingLogger();
        using var hostA = new FlowOrchestratorSqlMigrator(cs, hostALog);
        await hostA.StartAsync(CancellationToken.None);
        await WaitUntilBuildIsParkedAsync(cs);
        var hostBLog = new CapturingLogger();
        using var hostB = new FlowOrchestratorSqlMigrator(cs, hostBLog);
        var store = new SqlFlowRunStore(cs);
        string? startFailure, writeFailure;
        bool buildStillRunning;

        // Act
        try
        {
            var start = OutcomeWithinBudgetAsync(Task.Run(async () =>
            {
                await hostB.StartAsync(CancellationToken.None);
                await hostB.BackgroundIndexBuild;
            }));
            var write = OutcomeWithinBudgetAsync(
                store.StartRunAsync(Guid.NewGuid(), "ConcurrentStartFlow", Guid.NewGuid(), "manual", null, null));
            startFailure = await start;
            writeFailure = await write;
            buildStillRunning = !hostA.BackgroundIndexBuild.IsCompleted;
        }
        finally
        {
            holderTx.Rollback();
        }

        await hostA.BackgroundIndexBuild.WaitAsync(Budget);

        // Assert
        Assert.True(startFailure is null, $"The second host's start (and its background build) did not complete: {startFailure}");
        Assert.True(writeFailure is null, $"A FlowRuns write did not complete during the second start: {writeFailure}");
        Assert.True(buildStillRunning, "Host A's build was not in flight during the second start, so the test proved nothing.");
        Assert.Contains(hostBLog.Entries, e => e.EventId == 7209);
        Assert.DoesNotContain(hostBLog.Entries, e => e.EventId == 7206);
        Assert.Contains(hostALog.Entries, e => e.EventId == 7205);
        Assert.Equal(2, await SearchIndexCountAsync(cs));
    }

    private async Task<string> CreateSeededDatabaseWithoutSearchIndexesAsync(int seedRuns = SeedRuns)
    {
        var name = "fo_start_" + Guid.NewGuid().ToString("N")[..12];
        await using (var conn = new SqlConnection(_serverConnectionString))
        {
            await conn.ExecuteAsync($"CREATE DATABASE [{name}]");
        }

        var cs = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = name }.ConnectionString;
        using (var initial = new FlowOrchestratorSqlMigrator(cs, new CapturingLogger()))
        {
            await initial.StartAsync(CancellationToken.None);
            await initial.BackgroundIndexBuild;
        }

        await using (var conn = new SqlConnection(cs))
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO FlowRuns (Id, FlowId, FlowName, Status, TriggerKey)
                SELECT TOP (@seedRuns) NEWID(), NEWID(), N'StartFlow', N'Succeeded', N'manual'
                FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b;
                """,
                new { seedRuns },
                commandTimeout: 300);
        }

        await DropSearchIndexesAsync(cs);
        return cs;
    }

    private static async Task DropSearchIndexesAsync(string cs)
    {
        await using var conn = new SqlConnection(cs);
        await conn.ExecuteAsync("""
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowRuns_IdText' AND object_id = OBJECT_ID(N'FlowRuns'))
                DROP INDEX [IX_FlowRuns_IdText] ON [FlowRuns];
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowRuns_StartedAt_Search' AND object_id = OBJECT_ID(N'FlowRuns'))
                DROP INDEX [IX_FlowRuns_StartedAt_Search] ON [FlowRuns];
            """);
    }

    private static async Task<int> SearchIndexCountAsync(string cs)
    {
        await using var conn = new SqlConnection(cs);
        return await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'FlowRuns') AND name IN (N'IX_FlowRuns_IdText', N'IX_FlowRuns_StartedAt_Search')
            """);
    }

    /// <summary>
    /// Waits until a CREATE INDEX in the database is blocked on a lock. Another session's lock wait is only
    /// observable through the DMVs, so this polls them, bounded by <see cref="Budget"/>.
    /// </summary>
    private static async Task WaitUntilBuildIsParkedAsync(string cs)
    {
        const string ParkedSql = """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.dm_exec_requests
                WHERE database_id = DB_ID() AND command = N'CREATE INDEX' AND wait_type LIKE N'LCK[_]M[_]%')
            THEN 1 ELSE 0 END AS BIT)
            """;
        using var timeout = new CancellationTokenSource(Budget);
        await using var conn = new SqlConnection(cs);
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
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private sealed class CapturingLogger : ILogger<FlowOrchestratorSqlMigrator>
    {
        public ConcurrentQueue<(int EventId, LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((eventId.Id, logLevel, formatter(state, exception)));
    }
}
