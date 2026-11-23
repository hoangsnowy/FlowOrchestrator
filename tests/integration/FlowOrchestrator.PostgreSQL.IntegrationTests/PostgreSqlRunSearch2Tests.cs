using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// 2.0 run search against a real PostgreSQL: the trigram-indexed run search expression, the run-id prefix
/// at any age, deep search through the correlated EXISTS, keyset paging, the capped count, the migrator's
/// concurrent index build (idempotent, repairs an INVALID leftover), and correct results without the index.
/// Rows are scoped by a fresh flow id or a unique token because the container is shared by the class.
/// </summary>
public sealed partial class PostgreSqlFlowRunStoreTests
{
    private static readonly DateTimeOffset SearchNow = DateTimeOffset.UtcNow;

    private async Task<Guid> SeedAsync(Guid flowId, string flowName, DateTimeOffset? startedAt = null, string? output = null)
    {
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, flowName, runId, "manual", null, null);
        await _store.RecordStepStartAsync(runId, "fetch", "Fetch", null, null);
        await _store.RecordStepCompleteAsync(runId, "fetch", "Succeeded", output, null);
        if (startedAt is { } at)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.ExecuteAsync("UPDATE flow_runs SET started_at = @at WHERE id = @runId", new { at = at.ToUniversalTime(), runId });
        }

        return runId;
    }

    [Fact]
    public async Task Search_index_exists_and_is_valid_after_migration()
    {
        // Arrange
        await using var conn = new NpgsqlConnection(_connectionString);

        // Act
        var valid = await conn.ExecuteScalarAsync<bool?>(PostgreSqlFlowOrchestratorMigrator.RunSearchIndexStateSql);

        // Assert
        Assert.True(valid);
    }

    [Fact]
    public async Task Quick_search_hits_case_insensitively_and_misses_cleanly()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var name = "PgQuick" + Guid.NewGuid().ToString("N")[..8];
        var runId = await SeedAsync(flowId, name);

        // Act
        var hit = await _store.SearchRunsAsync(new RunSearchQuery { Text = name[2..].ToUpperInvariant(), StartedFrom = SearchNow.AddDays(-7) });
        var miss = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "zq-nomatch-7731", StartedFrom = SearchNow.AddDays(-7) });

        // Assert
        Assert.Equal(runId, Assert.Single(hit.Runs).Id);
        Assert.Empty(miss.Runs);
    }

    [Fact]
    public async Task Run_id_prefix_finds_a_sixty_day_old_run_inside_a_seven_day_window()
    {
        // Arrange
        var old = await SeedAsync(Guid.NewGuid(), "PgPrefixFlow", SearchNow.AddDays(-60));

        // Act
        var page = await _store.SearchRunsAsync(new RunSearchQuery { Text = old.ToString("D")[..8].ToUpperInvariant(), StartedFrom = SearchNow.AddDays(-7) });

        // Assert
        Assert.Contains(page.Runs, r => r.Id == old);
    }

    [Fact]
    public async Task Deep_search_finds_a_payload_id_inside_the_window_only()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var invoice = "INV-" + Guid.NewGuid().ToString("N")[..10];
        var recent = await SeedAsync(flowId, "PgDeepFlow", output: $$"""{"invoiceId":"{{invoice}}"}""");
        await SeedAsync(flowId, "PgDeepFlow", SearchNow.AddDays(-30), output: $$"""{"invoiceId":"{{invoice}}"}""");

        // Act
        var page = await _store.SearchRunsAsync(new RunSearchQuery { Text = invoice.ToLowerInvariant(), IncludeStepPayloads = true, StartedFrom = SearchNow.AddDays(-7) });

        // Assert
        Assert.Equal(recent, Assert.Single(page.Runs).Id);
    }

    [Fact]
    public async Task Keyset_paging_is_stable_while_new_runs_start()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++) seeded.Add(await SeedAsync(flowId, "PgKeyset", SearchNow.AddMinutes(-10 - i)));
        var first = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 2 });
        await SeedAsync(flowId, "PgKeyset", SearchNow.AddMinutes(1));

        // Act
        var second = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 2, After = first.NextCursor });
        var third = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 2, After = second.NextCursor });

        // Assert
        Assert.Equal(new[] { seeded[0], seeded[1] }, first.Runs.Select(r => r.Id));
        Assert.Equal(new[] { seeded[2], seeded[3] }, second.Runs.Select(r => r.Id));
        Assert.Equal(new[] { seeded[4] }, third.Runs.Select(r => r.Id));
    }

    [Fact]
    public async Task Capped_count_reports_a_lower_bound()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        for (var i = 0; i < 5; i++) await SeedAsync(flowId, "PgCap");

        // Act
        var capped = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 1, IncludeTotal = true, TotalCountCap = 3 });

        // Assert
        Assert.Equal(3, capped.TotalCount);
        Assert.True(capped.TotalIsLowerBound);
    }

    [Fact]
    public async Task Migration_repairs_an_invalid_index_and_search_works_without_it()
    {
        // Arrange — a fresh database whose search index was left INVALID (as an interrupted CONCURRENTLY build leaves it).
        var connectionString = await CreateDatabaseAsync();
        using (var first = new PostgreSqlFlowOrchestratorMigrator(connectionString, NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance))
        {
            await first.StartAsync(CancellationToken.None);
            await first.BackgroundIndexBuild;
        }

        var store = new PostgreSqlFlowRunStore(connectionString);
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "PgRepairFlow", runId, "manual", null, null);
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.ExecuteAsync("UPDATE pg_index SET indisvalid = false WHERE indexrelid = 'ix_flow_runs_run_search_trgm'::regclass");
        }

        var withoutIndex = await store.SearchRunsAsync(new RunSearchQuery { Text = "repairflow" });

        // Act
        using (var second = new PostgreSqlFlowOrchestratorMigrator(connectionString, NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance))
        {
            await second.StartAsync(CancellationToken.None);
            await second.BackgroundIndexBuild;
        }

        // Assert
        await using var check = new NpgsqlConnection(connectionString);
        Assert.True(await check.ExecuteScalarAsync<bool?>(PostgreSqlFlowOrchestratorMigrator.RunSearchIndexStateSql));
        Assert.Equal(runId, Assert.Single(withoutIndex.Runs).Id);
        Assert.Equal(runId, Assert.Single((await store.SearchRunsAsync(new RunSearchQuery { Text = "repairflow" })).Runs).Id);
    }

    /// <summary>Creates an empty database in the shared container and returns its connection string.</summary>
    private async Task<string> CreateDatabaseAsync()
    {
        var name = "fo_search_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(_connectionString))
            await admin.ExecuteAsync($"CREATE DATABASE {name}");
        return new NpgsqlConnectionStringBuilder(_connectionString) { Database = name }.ConnectionString;
    }
}
