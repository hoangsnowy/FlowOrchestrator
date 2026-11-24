using Dapper;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// 2.0 run search against a real SQL Server: narrow search column, run-id prefix at any age, keyset
/// paging, capped count, deep search with and without the token index, migration of a pre-2.0
/// database, and a resumed token back-fill. Rows are scoped by a fresh flow id or a unique token
/// because the container is shared by the class.
/// </summary>
public sealed partial class SqlFlowRunStoreTests
{
    private static readonly DateTimeOffset SearchNow = DateTimeOffset.UtcNow;

    private SqlFlowRunStore TokenStore(RunSearchIndexOptions? options = null)
        => new(_connectionString, new SqlServerRunSearchTokenIndex(_connectionString, options ?? new RunSearchIndexOptions { Enabled = true }));

    private async Task<Guid> SeedAsync(SqlFlowRunStore store, Guid flowId, string flowName, DateTimeOffset? startedAt = null, string? output = null, string? error = null, string triggerKey = "manual")
    {
        var runId = Guid.NewGuid();
        await store.StartRunAsync(flowId, flowName, runId, triggerKey, null, null);
        await store.RecordStepStartAsync(runId, "fetch", "Fetch", null, null);
        await store.RecordStepCompleteAsync(runId, "fetch", "Succeeded", output, error);
        if (startedAt is { } at)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync("UPDATE FlowRuns SET StartedAt = @at WHERE Id = @runId", new { at, runId });
        }

        return runId;
    }

    [Fact]
    public async Task Quick_search_hits_a_flow_name_fragment_case_insensitively_and_misses_cleanly()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var name = "QuickSearch" + Guid.NewGuid().ToString("N")[..8];
        var runId = await SeedAsync(_store, flowId, name);

        // Act
        var hit = await _store.SearchRunsAsync(new RunSearchQuery { Text = name[5..].ToUpperInvariant(), StartedFrom = SearchNow.AddDays(-7) });
        var miss = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "zq-nomatch-7731", StartedFrom = SearchNow.AddDays(-7) });

        // Assert
        Assert.Equal(runId, Assert.Single(hit.Runs).Id);
        Assert.Empty(miss.Runs);
        Assert.False(miss.HasMore);
    }

    [Fact]
    public async Task Run_id_prefix_finds_a_sixty_day_old_run_inside_a_seven_day_window()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var old = await SeedAsync(_store, flowId, "PrefixFlow", SearchNow.AddDays(-60));

        // Act
        var page = await _store.SearchRunsAsync(new RunSearchQuery { Text = old.ToString("D")[..8], StartedFrom = SearchNow.AddDays(-7) });

        // Assert
        Assert.Contains(page.Runs, r => r.Id == old);
    }

    [Fact]
    public async Task Keyset_paging_is_stable_while_new_runs_start()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++) seeded.Add(await SeedAsync(_store, flowId, "KeysetFlow", SearchNow.AddMinutes(-10 - i)));
        var first = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 2 });
        await SeedAsync(_store, flowId, "KeysetFlow", SearchNow.AddMinutes(1));

        // Act
        var second = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 2, After = first.NextCursor });
        var third = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 2, After = second.NextCursor });

        // Assert
        Assert.Equal(new[] { seeded[0], seeded[1] }, first.Runs.Select(r => r.Id));
        Assert.Equal(new[] { seeded[2], seeded[3] }, second.Runs.Select(r => r.Id));
        Assert.Equal(new[] { seeded[4] }, third.Runs.Select(r => r.Id));
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task Capped_count_reports_a_lower_bound()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        for (var i = 0; i < 5; i++) await SeedAsync(_store, flowId, "CapFlow");

        // Act
        var capped = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 1, IncludeTotal = true, TotalCountCap = 3 });
        var exact = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 1, IncludeTotal = true, TotalCountCap = 10 });

        // Assert
        Assert.Equal(3, capped.TotalCount);
        Assert.True(capped.TotalIsLowerBound);
        Assert.Equal(5, exact.TotalCount);
        Assert.False(exact.TotalIsLowerBound);
    }

    [Fact]
    public async Task Deep_search_without_the_token_index_finds_a_payload_id_by_bin2_like()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var invoice = "INV-" + Guid.NewGuid().ToString("N")[..10];
        var runId = await SeedAsync(_store, flowId, "DeepLikeFlow", output: $$"""{"invoiceId":"{{invoice}}"}""");

        // Act
        var page = await _store.SearchRunsAsync(new RunSearchQuery { Text = invoice.ToLowerInvariant(), IncludeStepPayloads = true, StartedFrom = SearchNow.AddDays(-7) });

        // Assert
        Assert.Equal(runId, Assert.Single(page.Runs).Id);
    }

    [Fact]
    public async Task Deep_search_with_the_token_index_finds_a_payload_id_from_the_token_table()
    {
        // Arrange — the payload is erased after indexing, so only the token table can still find it.
        var store = TokenStore();
        var flowId = Guid.NewGuid();
        var invoice = "INV-" + Guid.NewGuid().ToString("N")[..10];
        var runId = await SeedAsync(store, flowId, "DeepTokenFlow", output: $$"""{"invoiceId":"{{invoice}}"}""");
        await using (var conn = new SqlConnection(_connectionString))
            await conn.ExecuteAsync("UPDATE FlowSteps SET OutputJson = NULL WHERE RunId = @runId", new { runId });

        // Act
        var page = await store.SearchRunsAsync(new RunSearchQuery { Text = invoice, IncludeStepPayloads = true, StartedFrom = SearchNow.AddDays(-7) });
        var withoutIndex = await _store.SearchRunsAsync(new RunSearchQuery { Text = invoice, IncludeStepPayloads = true, StartedFrom = SearchNow.AddDays(-7) });

        // Assert
        Assert.Equal(runId, Assert.Single(page.Runs).Id);
        Assert.Empty(withoutIndex.Runs);
    }

    [Fact]
    public async Task Migration_upgrades_a_pre_2_0_database_and_is_idempotent()
    {
        // Arrange — a FlowRuns table in the 1.34 shape (no computed columns) with a row in it.
        var connectionString = await CreateDatabaseAsync();
        var runId = Guid.NewGuid();
        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.ExecuteAsync("""
                CREATE TABLE FlowRuns (
                    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY, FlowId UNIQUEIDENTIFIER NOT NULL, FlowName NVARCHAR(256) NULL,
                    Status NVARCHAR(64) NOT NULL, TriggerKey NVARCHAR(256) NULL, TriggerDataJson NVARCHAR(MAX) NULL,
                    BackgroundJobId NVARCHAR(128) NULL, StartedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                    CompletedAt DATETIMEOFFSET NULL);
                INSERT INTO FlowRuns (Id, FlowId, FlowName, Status, TriggerKey) VALUES (@runId, NEWID(), N'LegacyFlow', N'Succeeded', N'Order-Created');
                """, new { runId });
        }

        // Act
        for (var pass = 0; pass < 2; pass++)
        {
            using var migrator = new FlowOrchestratorSqlMigrator(connectionString, NullLogger<FlowOrchestratorSqlMigrator>.Instance);
            await migrator.StartAsync(CancellationToken.None);
            await migrator.BackgroundIndexBuild;
        }

        // Assert
        await using var check = new SqlConnection(connectionString);
        var searchText = await check.ExecuteScalarAsync<string>("SELECT RunSearchText FROM FlowRuns WHERE Id = @runId", new { runId });
        var indexes = (await check.QueryAsync<string>(
            "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'FlowRuns') AND name IN (N'IX_FlowRuns_IdText', N'IX_FlowRuns_StartedAt_Search')")).ToList();
        Assert.Equal($"{runId:D}|legacyflow|order-created|succeeded|", searchText);
        Assert.Equal(2, indexes.Count);
        var page = await new SqlFlowRunStore(connectionString).SearchRunsAsync(new RunSearchQuery { Text = runId.ToString("D")[..8] });
        Assert.Equal(runId, Assert.Single(page.Runs).Id);
    }

    [Fact]
    public async Task Backfill_resumes_after_the_stored_cursor_and_marks_itself_complete()
    {
        // Arrange — five runs written without the index; a previous pass already covered the two newest.
        var connectionString = await CreateDatabaseAsync();
        using (var migrator = new FlowOrchestratorSqlMigrator(connectionString, NullLogger<FlowOrchestratorSqlMigrator>.Instance, runSearchIndexEnabled: true))
        {
            await migrator.StartAsync(CancellationToken.None);
            await migrator.BackgroundIndexBuild;
        }

        var plain = new SqlFlowRunStore(connectionString);
        var runs = new List<(Guid Id, string Token)>();
        for (var i = 0; i < 5; i++)
        {
            var token = "BF-" + Guid.NewGuid().ToString("N")[..10];
            var runId = Guid.NewGuid();
            await plain.StartRunAsync(Guid.NewGuid(), "BackfillFlow", runId, "manual", null, null);
            await plain.RecordStepStartAsync(runId, "fetch", "Fetch", null, null);
            await plain.RecordStepCompleteAsync(runId, "fetch", "Succeeded", $$"""{"ref":"{{token}}"}""", null);
            await using var c = new SqlConnection(connectionString);
            await c.ExecuteAsync("UPDATE FlowRuns SET StartedAt = @at WHERE Id = @runId", new { at = SearchNow.AddMinutes(-i), runId });
            runs.Add((runId, token));
        }

        var resumeAfter = runs[1];
        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.ExecuteAsync(
                "UPDATE FlowRunSearchIndexState SET BackfillAfterStartedAt = @at, BackfillAfterId = @id WHERE Id = 1",
                new { at = SearchNow.AddMinutes(-1), id = resumeAfter.Id });
        }

        var options = new RunSearchIndexOptions { Enabled = true, BackfillBatchSize = 2, BackfillBatchDelay = TimeSpan.Zero };
        var service = new SqlRunSearchIndexBackfillService(
            connectionString, new SqlServerRunSearchTokenIndex(connectionString, options), NullLogger<SqlRunSearchIndexBackfillService>.Instance);

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.BackfillPass.WaitAsync(TimeSpan.FromSeconds(60));
        await service.StopAsync(CancellationToken.None);

        // Assert
        await using var check = new SqlConnection(connectionString);
        var indexed = (await check.QueryAsync<Guid>("SELECT DISTINCT RunId FROM FlowRunSearchTokens")).ToHashSet();
        var completedAt = await check.ExecuteScalarAsync<DateTimeOffset?>("SELECT BackfillCompletedAt FROM FlowRunSearchIndexState WHERE Id = 1");
        Assert.DoesNotContain(runs[0].Id, indexed);
        Assert.DoesNotContain(runs[1].Id, indexed);
        Assert.Contains(runs[2].Id, indexed);
        Assert.Contains(runs[3].Id, indexed);
        Assert.Contains(runs[4].Id, indexed);
        Assert.NotNull(completedAt);
    }

    /// <summary>Creates an empty database in the shared container and returns its connection string.</summary>
    private async Task<string> CreateDatabaseAsync()
    {
        var name = "fo_search_" + Guid.NewGuid().ToString("N")[..12];
        await using (var master = new SqlConnection(_connectionString))
            await master.ExecuteAsync($"CREATE DATABASE [{name}]");
        return new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = name }.ConnectionString;
    }
}
