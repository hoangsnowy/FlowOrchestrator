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
            var outcomes = await Task.WhenAll(starts.Select(OutcomeWithinBudgetAsync));
            failures.AddRange(outcomes.OfType<string>().Select(failure => $"round {round}: {failure}"));

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

    [Fact]
    public async Task Open_step_index_is_built_online_in_the_background_and_neither_start_nor_step_writes_wait_for_it()
    {
        // Arrange — an up-to-date schema without IX_FlowSteps_RunId_Open (an upgraded 1.x database), and an open
        // write transaction on FlowSteps that parks the online build in its prepare phase.
        var cs = await CreateSeededDatabaseWithoutSearchIndexesAsync(seedRuns: 1);
        await RestoreSearchIndexesAsync(cs);
        await DropOpenStepIndexAsync(cs);
        var store = new SqlFlowRunStore(cs);
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "OpenStepIndexFlow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "held", "Work", null, null);
        await store.RecordStepStartAsync(runId, "a", "Work", null, null);
        await using var holder = new SqlConnection(cs);
        await holder.OpenAsync();
        var holderTx = holder.BeginTransaction();
        await holder.ExecuteAsync("UPDATE FlowSteps SET Status = Status WHERE RunId = @runId AND StepKey = N'held'", new { runId }, holderTx);
        var log = new CapturingLogger();
        using var host = new FlowOrchestratorSqlMigrator(cs, log);
        string? startFailure, writeFailure;
        bool buildStillRunning;

        // Act — the host starts while its build is parked, and a running host keeps writing steps.
        try
        {
            startFailure = await OutcomeWithinBudgetAsync(host.StartAsync(CancellationToken.None));
            await WaitUntilBuildIsParkedAsync(cs);
            // An existing row: a new one would take a key-range lock next to the held row and wait for the
            // holder itself, which says nothing about the build.
            writeFailure = await OutcomeWithinBudgetAsync(
                store.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null));
            buildStillRunning = !host.BackgroundIndexBuild.IsCompleted;
        }
        finally
        {
            holderTx.Rollback();
        }

        await host.BackgroundIndexBuild.WaitAsync(Budget);

        // Assert
        Assert.True(startFailure is null, $"The start waited for the open-step index build: {startFailure}");
        Assert.True(writeFailure is null, $"A FlowSteps write waited for the open-step index build: {writeFailure}");
        Assert.True(buildStillRunning, "The build was not in flight during the writes, so the test proved nothing.");
        Assert.True(await OpenStepIndexExistsAsync(cs));
        Assert.Contains(log.Entries, e => e.EventId == 7217);
        Assert.DoesNotContain(log.Entries, e => e.EventId == 7218);
    }

    [Fact]
    public async Task Open_step_index_on_an_edition_without_online_builds_is_built_at_start_while_the_table_is_small()
    {
        // Arrange — the offline policy the run-search indexes follow: synchronous below OfflineIndexBuildRowLimit.
        var cs = await CreateSeededDatabaseWithoutSearchIndexesAsync(seedRuns: 1);
        await RestoreSearchIndexesAsync(cs);
        await DropOpenStepIndexAsync(cs);
        using var host = new FlowOrchestratorSqlMigrator(cs, new CapturingLogger()) { OnlineIndexBuildOverride = false };

        // Act
        await host.StartAsync(CancellationToken.None);

        // Assert
        Assert.True(host.BackgroundIndexBuild.IsCompleted);
        Assert.True(await OpenStepIndexExistsAsync(cs));
    }

    [Fact]
    public async Task Open_row_check_is_correct_before_the_open_step_index_exists()
    {
        // Arrange — no index at all: the store's query must still answer from the rows.
        var cs = await CreateSeededDatabaseWithoutSearchIndexesAsync(seedRuns: 1);
        await DropOpenStepIndexAsync(cs);
        var store = new SqlFlowRunStore(cs);
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "OpenStepIndexFlow", runId, "manual", null, null);

        // Act
        var empty = await store.HasRunningOrPendingStepAsync(runId);
        await store.RecordStepStartAsync(runId, "a", "Work", null, null);
        var running = await store.HasRunningOrPendingStepAsync(runId);
        await store.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
        var closed = await store.HasRunningOrPendingStepAsync(runId);

        // Assert
        Assert.False(await OpenStepIndexExistsAsync(cs));
        Assert.False(empty);
        Assert.True(running);
        Assert.False(closed);
    }

    private static async Task RestoreSearchIndexesAsync(string cs)
    {
        await using var conn = new SqlConnection(cs);
        await conn.ExecuteAsync(FlowOrchestratorSqlMigrator.BuildRunSearchIndexesSql(online: false));
    }

    private static async Task DropOpenStepIndexAsync(string cs)
    {
        await using var conn = new SqlConnection(cs);
        await conn.ExecuteAsync("""
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowSteps_RunId_Open' AND object_id = OBJECT_ID(N'FlowSteps'))
                DROP INDEX [IX_FlowSteps_RunId_Open] ON [FlowSteps];
            """);
    }

    private static async Task<bool> OpenStepIndexExistsAsync(string cs)
    {
        await using var conn = new SqlConnection(cs);
        return await conn.ExecuteScalarAsync<bool>("""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowSteps_RunId_Open' AND object_id = OBJECT_ID(N'FlowSteps'))
            THEN 1 ELSE 0 END AS BIT)
            """);
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
        // Read the outcome from the task instead of a catch-all: whatever ended it (WaitAsync's
        // TimeoutException, or the migrator's own failure) is reported by type and message.
        var waited = task.WaitAsync(Budget);
        await waited.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (waited.IsCanceled)
            return "TaskCanceledException: A task was canceled.";
        return waited.Exception?.InnerException is { } ex ? $"{ex.GetType().Name}: {ex.Message}" : null;
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
