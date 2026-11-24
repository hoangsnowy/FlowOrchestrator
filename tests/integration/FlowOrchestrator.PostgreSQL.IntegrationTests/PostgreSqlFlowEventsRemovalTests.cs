using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// 2.0 removed the persisted run-event log. The migrator no longer creates <c>flow_events</c> on a
/// new database, never drops it on an upgraded one (a 1.34.x rollback must still read its own
/// history), and the retention sweep must work whether or not the table is there.
/// </summary>
/// <remarks>
/// Each test migrates its own database inside the shared container, so the "fresh" and "upgraded"
/// schemas never see each other. <c>MigrateAsync</c> deliberately does not wait for the migrator's
/// background run-search index build: the upgrade test's second start overlaps the first start's build,
/// as a second replica or a restarted host does. That overlap used to fail intermittently with
/// <c>40P01</c>; <see cref="PostgreSqlMigratorConcurrentIndexBuildTests"/> pins it.
/// </remarks>
public sealed class PostgreSqlFlowEventsRemovalTests : IClassFixture<PostgreSqlFixture>
{
    // The flow_events DDL exactly as the 1.34.x migrator issued it. Every other table in the 1.34.3
    // schema is identical to what the 2.0 migrator creates, so "2.0 schema + this block" is a
    // 1.34.3 database.
    private const string FlowEvents134Ddl = """
        CREATE TABLE IF NOT EXISTS flow_events (
            sequence      BIGSERIAL    PRIMARY KEY,
            run_id        UUID         NOT NULL,
            timestamp_utc TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            type          VARCHAR(128) NOT NULL,
            step_key      VARCHAR(256) NULL,
            message       TEXT         NULL
        );

        CREATE INDEX IF NOT EXISTS ix_flow_events_run_id_sequence ON flow_events (run_id, sequence);
        """;

    private readonly string _serverConnectionString;

    public PostgreSqlFlowEventsRemovalTests(PostgreSqlFixture fixture)
    {
        _serverConnectionString = fixture.ConnectionString;
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var name = "fo_events_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(_serverConnectionString))
        {
            await conn.ExecuteAsync($"CREATE DATABASE {name}");
        }

        return new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = name }.ConnectionString;
    }

    private static Task MigrateAsync(string connectionString) =>
        new PostgreSqlFlowOrchestratorMigrator(connectionString, NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance)
            .StartAsync(CancellationToken.None);

    private static async Task<bool> FlowEventsExistsAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        return await conn.ExecuteScalarAsync<bool>("SELECT to_regclass('flow_events') IS NOT NULL");
    }

    /// <summary>Runs one run to completion, backdates it past the cutoff, and returns its id.</summary>
    private static async Task<Guid> SeedExpiredRunAsync(PostgreSqlFlowRunStore store, string connectionString)
    {
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "a", "Work", null, null);
        await store.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
        await store.CompleteRunAsync(runId, "Succeeded");
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.ExecuteAsync(
            "UPDATE flow_runs SET completed_at = NOW() - INTERVAL '30 days' WHERE id = @runId",
            new { runId });
        return runId;
    }

    [Fact]
    public async Task Fresh_database_has_no_flow_events_table_and_retention_cleanup_succeeds()
    {
        // Arrange
        var cs = await CreateDatabaseAsync();
        await MigrateAsync(cs);
        var store = new PostgreSqlFlowRunStore(cs);
        var runId = await SeedExpiredRunAsync(store, cs);

        // Act
        var ex = await Record.ExceptionAsync(() => store.CleanupAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));

        // Assert
        Assert.Null(ex);
        Assert.False(await FlowEventsExistsAsync(cs));
        Assert.Null(await store.GetRunAsync(runId));
    }

    [Fact]
    public async Task Database_upgraded_from_1_34_keeps_flow_events_untouched_and_retention_cleanup_succeeds()
    {
        // Arrange — a 1.34.x database with history in FlowEvents, then a 2.0 host starting on it.
        var cs = await CreateDatabaseAsync();
        await MigrateAsync(cs);
        var legacyRunId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.ExecuteAsync(FlowEvents134Ddl);
            await conn.ExecuteAsync(
                "INSERT INTO flow_events (run_id, type, step_key, message) VALUES (@legacyRunId, 'run.started', NULL, 'legacy')",
                new { legacyRunId });
        }

        await MigrateAsync(cs);
        var store = new PostgreSqlFlowRunStore(cs);
        var runId = await SeedExpiredRunAsync(store, cs);

        // Act
        var ex = await Record.ExceptionAsync(() => store.CleanupAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));

        // Assert — the sweep worked, and 2.0 neither dropped nor emptied the legacy table.
        Assert.Null(ex);
        Assert.Null(await store.GetRunAsync(runId));
        Assert.True(await FlowEventsExistsAsync(cs));
        await using var check = new NpgsqlConnection(cs);
        Assert.Equal(1L, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM flow_events WHERE run_id = @legacyRunId", new { legacyRunId }));
    }

    [Fact]
    public async Task Run_detail_carries_the_attempts_the_derived_timeline_is_built_from()
    {
        // Arrange — the dashboard's Events drawer reads GetRunDetailAsync + FlowRunTimeline.Build.
        var cs = await CreateDatabaseAsync();
        await MigrateAsync(cs);
        var store = new PostgreSqlFlowRunStore(cs);
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "a", "Work", null, null);
        await store.RecordStepCompleteAsync(runId, "a", "Failed", null, "boom");
        await store.RecordStepStartAsync(runId, "a", "Work", null, null);
        await store.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
        await store.CompleteRunAsync(runId, "Succeeded");
        var run = await store.GetRunDetailAsync(runId);

        // Act
        var timeline = FlowRunTimeline.Build(run!);

        // Assert
        Assert.Equal(
            ["run.started", "step.started", "step.failed", "step.started", "step.completed", "run.completed"],
            timeline.Select(e => e.Type));
        Assert.Equal("boom", timeline[2].Message);
    }
}
