using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// 2.0 removed the persisted run-event log. The migrator no longer creates <c>FlowEvents</c> on a
/// new database, never drops it on an upgraded one (a 1.34.x rollback must still read its own
/// history), and the retention sweep must work whether or not the table is there.
/// </summary>
/// <remarks>
/// Each test migrates its own database inside the shared container, so the "fresh" and "upgraded"
/// schemas never see each other.
/// </remarks>
public sealed class SqlFlowEventsRemovalTests : IClassFixture<SqlServerFixture>
{
    // The FlowEvents DDL exactly as the 1.34.x migrator issued it. Every other table in the 1.34.3
    // schema is identical to what the 2.0 migrator creates, so "2.0 schema + this block" is a
    // 1.34.3 database.
    private const string FlowEvents134Ddl = """
        CREATE TABLE [FlowEvents] (
            [Sequence]   BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            [RunId]      UNIQUEIDENTIFIER NOT NULL,
            [Timestamp]  DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
            [Type]       NVARCHAR(128)    NOT NULL,
            [StepKey]    NVARCHAR(256)    NULL,
            [Message]    NVARCHAR(MAX)    NULL
        );

        CREATE INDEX IX_FlowEvents_RunId_Sequence ON [FlowEvents]([RunId], [Sequence]);
        """;

    private readonly string _serverConnectionString;

    public SqlFlowEventsRemovalTests(SqlServerFixture fixture)
    {
        _serverConnectionString = fixture.ConnectionString;
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var name = "fo_events_" + Guid.NewGuid().ToString("N");
        await using (var conn = new SqlConnection(_serverConnectionString))
        {
            await conn.ExecuteAsync($"CREATE DATABASE [{name}]");
        }

        return new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    private static Task MigrateAsync(string connectionString) =>
        new FlowOrchestratorSqlMigrator(connectionString, NullLogger<FlowOrchestratorSqlMigrator>.Instance)
            .StartAsync(CancellationToken.None);

    private static async Task<bool> FlowEventsExistsAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'FlowEvents'") == 1;
    }

    /// <summary>Runs one run to completion, backdates it past the cutoff, and returns its id.</summary>
    private static async Task<Guid> SeedExpiredRunAsync(SqlFlowRunStore store, string connectionString)
    {
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "a", "Work", null, null);
        await store.RecordStepCompleteAsync(runId, "a", "Succeeded", null, null);
        await store.CompleteRunAsync(runId, "Succeeded");
        await using var conn = new SqlConnection(connectionString);
        await conn.ExecuteAsync(
            "UPDATE FlowRuns SET CompletedAt = DATEADD(day, -30, SYSDATETIMEOFFSET()) WHERE Id = @runId",
            new { runId });
        return runId;
    }

    [Fact]
    public async Task Fresh_database_has_no_FlowEvents_table_and_retention_cleanup_succeeds()
    {
        // Arrange
        var cs = await CreateDatabaseAsync();
        await MigrateAsync(cs);
        var store = new SqlFlowRunStore(cs);
        var runId = await SeedExpiredRunAsync(store, cs);

        // Act
        var ex = await Record.ExceptionAsync(() => store.CleanupAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));

        // Assert
        Assert.Null(ex);
        Assert.False(await FlowEventsExistsAsync(cs));
        Assert.Null(await store.GetRunAsync(runId));
    }

    [Fact]
    public async Task Database_upgraded_from_1_34_keeps_FlowEvents_untouched_and_retention_cleanup_succeeds()
    {
        // Arrange — a 1.34.x database with history in FlowEvents, then a 2.0 host starting on it.
        var cs = await CreateDatabaseAsync();
        await MigrateAsync(cs);
        var legacyRunId = Guid.NewGuid();
        await using (var conn = new SqlConnection(cs))
        {
            await conn.ExecuteAsync(FlowEvents134Ddl);
            await conn.ExecuteAsync(
                "INSERT INTO FlowEvents (RunId, Type, StepKey, Message) VALUES (@legacyRunId, 'run.started', NULL, 'legacy')",
                new { legacyRunId });
        }

        await MigrateAsync(cs);
        var store = new SqlFlowRunStore(cs);
        var runId = await SeedExpiredRunAsync(store, cs);

        // Act
        var ex = await Record.ExceptionAsync(() => store.CleanupAsync(DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));

        // Assert — the sweep worked, and 2.0 neither dropped nor emptied the legacy table.
        Assert.Null(ex);
        Assert.Null(await store.GetRunAsync(runId));
        Assert.True(await FlowEventsExistsAsync(cs));
        await using var check = new SqlConnection(cs);
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM FlowEvents WHERE RunId = @legacyRunId", new { legacyRunId }));
    }

    [Fact]
    public async Task Run_detail_carries_the_attempts_the_derived_timeline_is_built_from()
    {
        // Arrange — the dashboard's Events drawer reads GetRunDetailAsync + FlowRunTimeline.Build.
        var cs = await CreateDatabaseAsync();
        await MigrateAsync(cs);
        var store = new SqlFlowRunStore(cs);
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
