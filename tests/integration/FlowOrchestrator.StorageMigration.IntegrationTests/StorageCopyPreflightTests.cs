using Dapper;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.PostgreSQL;
using FlowOrchestrator.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FlowOrchestrator.StorageMigration.IntegrationTests;

/// <summary>
/// The refusals (in-flight runs, non-empty target, outdated or newer schema), the dry run, resume, the
/// verification catching a tampered target, a row the target cannot store, and the legacy event table —
/// each against real databases, with data written through the real stores.
/// </summary>
public sealed class StorageCopyPreflightTests
{
    private readonly DatabaseFixture _db;

    public StorageCopyPreflightTests(DatabaseFixture db) => _db = db;

    private static FlowStorageCopyOptions Options(FlowStorageProvider source, string sourceCs, FlowStorageProvider target, string targetCs) => new()
    {
        SourceProvider = source, SourceConnectionString = sourceCs,
        TargetProvider = target, TargetConnectionString = targetCs,
        BatchSize = 3,
    };

    private static async Task MigrateAsync(FlowStorageProvider provider, string cs)
    {
        if (provider == FlowStorageProvider.SqlServer)
        {
            using var migrator = new FlowOrchestratorSqlMigrator(cs, NullLogger<FlowOrchestratorSqlMigrator>.Instance);
            await migrator.StartAsync(CancellationToken.None);
            await migrator.BackgroundIndexBuild;
        }
        else
        {
            using var migrator = new PostgreSqlFlowOrchestratorMigrator(cs, NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance);
            await migrator.StartAsync(CancellationToken.None);
            await migrator.BackgroundIndexBuild;
        }
    }

    /// <summary>A migrated database with one flow and one completed two-step run, written through the stores.</summary>
    private async Task<(string Cs, Guid RunId)> SourceWithOneRunAsync(FlowStorageProvider provider, string? outputJson = null, string status = "Succeeded")
    {
        var cs = await _db.CreateDatabaseAsync(provider);
        await MigrateAsync(provider, cs);
        await using var store = StoreReader.Create(provider, cs);
        var flowId = Guid.NewGuid();
        await store.Flows.SaveAsync(new FlowDefinitionRecord { Id = flowId, Name = "SmallFlow", Version = "1.0", ManifestJson = "{}", IsEnabled = true });
        var runId = Guid.NewGuid();
        await store.Runs.StartRunAsync(flowId, "SmallFlow", runId, "manual", "{\"n\":1}", "job-1");
        await store.Runs.RecordStepStartAsync(runId, "a", "Echo", "{}", "job-a");
        await store.Runs.RecordStepCompleteAsync(runId, "a", "Succeeded", "{\"ok\":true}", null);
        await store.Runs.RecordStepStartAsync(runId, "b", "Echo", "{}", "job-b");
        await store.Runs.RecordStepCompleteAsync(runId, "b", "Succeeded", outputJson ?? "{\"ok\":2}", null);
        if (status != "Running")
            await store.Runs.CompleteRunAsync(runId, status);
        return (cs, runId);
    }

    private static async Task<bool> HasFlowRunsTableAsync(FlowStorageProvider provider, string cs)
    {
        if (provider == FlowStorageProvider.SqlServer)
        {
            await using var conn = new SqlConnection(cs);
            return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'FlowRuns'") == 1;
        }

        await using var pg = new NpgsqlConnection(cs);
        return await pg.ExecuteScalarAsync<bool>("SELECT to_regclass('flow_runs') IS NOT NULL");
    }

    [Fact]
    public async Task A_source_with_running_runs_is_refused_and_the_target_is_left_untouched()
    {
        // Arrange
        var (sourceCs, runId) = await SourceWithOneRunAsync(FlowStorageProvider.SqlServer, status: "Running");
        var targetCs = await _db.CreatePostgreSqlDatabaseAsync();
        var copier = new FlowStorageCopier(Options(FlowStorageProvider.SqlServer, sourceCs, FlowStorageProvider.PostgreSql, targetCs));

        // Act
        var ex = await Assert.ThrowsAsync<FlowStoragePreflightException>(() => copier.CopyAsync());

        // Assert
        Assert.Equal(FlowStoragePreflightFailure.ActiveRuns, ex.Failure);
        Assert.Contains("1 run(s) in status Running", ex.Message);
        Assert.Contains(runId.ToString(), ex.Message);
        Assert.False(await HasFlowRunsTableAsync(FlowStorageProvider.PostgreSql, targetCs));
    }

    [Fact]
    public async Task Force_copies_running_runs_as_they_are_and_says_so()
    {
        // Arrange
        var (sourceCs, runId) = await SourceWithOneRunAsync(FlowStorageProvider.PostgreSql, status: "Running");
        var targetCs = await _db.CreateSqlServerDatabaseAsync();
        var options = Options(FlowStorageProvider.PostgreSql, sourceCs, FlowStorageProvider.SqlServer, targetCs);
        options.Force = true;

        // Act
        var result = await new FlowStorageCopier(options).CopyAsync();

        // Assert
        Assert.True(result.IsVerified);
        Assert.Contains(result.Notes, n => n.Contains("--force", StringComparison.Ordinal));
        await using var target = StoreReader.Create(FlowStorageProvider.SqlServer, targetCs);
        Assert.Equal("Running", (await target.Runs.GetRunDetailAsync(runId))!.Status);
    }

    [Fact]
    public async Task A_target_that_already_holds_FlowOrchestrator_rows_is_refused()
    {
        // Arrange — the usual mistake: the new host was started on the target before the copy.
        var (sourceCs, _) = await SourceWithOneRunAsync(FlowStorageProvider.SqlServer);
        var targetCs = await _db.CreatePostgreSqlDatabaseAsync();
        await MigrateAsync(FlowStorageProvider.PostgreSql, targetCs);
        await using (var target = StoreReader.Create(FlowStorageProvider.PostgreSql, targetCs))
            await target.Flows.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = "Synced", Version = "1.0", IsEnabled = true });
        var copier = new FlowStorageCopier(Options(FlowStorageProvider.SqlServer, sourceCs, FlowStorageProvider.PostgreSql, targetCs));

        // Act
        var ex = await Assert.ThrowsAsync<FlowStoragePreflightException>(() => copier.CopyAsync());

        // Assert
        Assert.Equal(FlowStoragePreflightFailure.TargetNotEmpty, ex.Failure);
        Assert.Contains("flow_definitions", ex.Message);
        await using var conn = new NpgsqlConnection(targetCs);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM flow_runs"));
    }

    [Fact]
    public async Task A_dry_run_counts_the_rows_and_writes_nothing_not_even_the_schema()
    {
        // Arrange
        var (sourceCs, _) = await SourceWithOneRunAsync(FlowStorageProvider.PostgreSql);
        var targetCs = await _db.CreateSqlServerDatabaseAsync();
        var options = Options(FlowStorageProvider.PostgreSql, sourceCs, FlowStorageProvider.SqlServer, targetCs);
        options.DryRun = true;

        // Act
        var result = await new FlowStorageCopier(options).CopyAsync();

        // Assert
        Assert.True(result.DryRun);
        Assert.True(result.TargetSchemaCreated);
        Assert.Equal(1, result.Tables.Single(t => t.Table == "FlowRuns").SourceRows);
        Assert.Equal(2, result.Tables.Single(t => t.Table == "FlowSteps").SourceRows);
        Assert.Equal(0, result.TotalCopiedRows);
        Assert.False(await HasFlowRunsTableAsync(FlowStorageProvider.SqlServer, targetCs));
        // The Testcontainers SQL Server connection string sets TrustServerCertificate=True: honoured, but reported.
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("The target connection (SQL Server ", warning, StringComparison.Ordinal);
        Assert.Contains("TrustServerCertificate=True", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_outdated_source_is_refused_and_migrate_source_upgrades_it_in_place()
    {
        // Arrange — a database whose flow_runs predates the re-run lineage column.
        var (sourceCs, _) = await SourceWithOneRunAsync(FlowStorageProvider.PostgreSql);
        await using (var conn = new NpgsqlConnection(sourceCs))
            await conn.ExecuteAsync("ALTER TABLE flow_runs DROP COLUMN source_run_id");
        var targetCs = await _db.CreateSqlServerDatabaseAsync();
        var options = Options(FlowStorageProvider.PostgreSql, sourceCs, FlowStorageProvider.SqlServer, targetCs);

        // Act
        var refused = await Assert.ThrowsAsync<FlowStoragePreflightException>(() => new FlowStorageCopier(options).CopyAsync());
        options.MigrateSourceSchema = true;
        var result = await new FlowStorageCopier(options).CopyAsync();

        // Assert
        Assert.Equal(FlowStoragePreflightFailure.SourceSchemaOutdated, refused.Failure);
        Assert.Contains("flow_runs.source_run_id", refused.Message);
        Assert.True(result.IsVerified);
        Assert.Contains(result.Notes, n => n.Contains("--migrate-source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_column_the_tool_does_not_know_is_refused_instead_of_dropped()
    {
        // Arrange
        var (sourceCs, _) = await SourceWithOneRunAsync(FlowStorageProvider.SqlServer);
        await using (var conn = new SqlConnection(sourceCs))
            await conn.ExecuteAsync("ALTER TABLE FlowSteps ADD RetryPolicyJson NVARCHAR(MAX) NULL");
        var targetCs = await _db.CreatePostgreSqlDatabaseAsync();
        var copier = new FlowStorageCopier(Options(FlowStorageProvider.SqlServer, sourceCs, FlowStorageProvider.PostgreSql, targetCs));

        // Act
        var ex = await Assert.ThrowsAsync<FlowStoragePreflightException>(() => copier.CopyAsync());

        // Assert
        Assert.Equal(FlowStoragePreflightFailure.SchemaNewerThanTool, ex.Failure);
        Assert.Contains("FlowSteps.RetryPolicyJson", ex.Message);
    }

    [Fact]
    public async Task Resume_copies_only_the_tables_that_are_still_empty()
    {
        // Arrange — a copy whose last tables never committed (simulated by emptying one on the target).
        var (sourceCs, _) = await SourceWithOneRunAsync(FlowStorageProvider.SqlServer);
        var targetCs = await _db.CreatePostgreSqlDatabaseAsync();
        var options = Options(FlowStorageProvider.SqlServer, sourceCs, FlowStorageProvider.PostgreSql, targetCs);
        await new FlowStorageCopier(options).CopyAsync();
        await using (var conn = new NpgsqlConnection(targetCs))
            await conn.ExecuteAsync("DELETE FROM flow_steps");

        // Act
        var refused = await Assert.ThrowsAsync<FlowStoragePreflightException>(() => new FlowStorageCopier(options).CopyAsync());
        options.Resume = true;
        var resumed = await new FlowStorageCopier(options).CopyAsync();

        // Assert
        Assert.Equal(FlowStoragePreflightFailure.TargetNotEmpty, refused.Failure);
        Assert.True(resumed.IsVerified);
        var steps = resumed.Tables.Single(t => t.Table == "FlowSteps");
        Assert.Equal(2, steps.CopiedRows);
        Assert.False(steps.SkippedAsComplete);
        Assert.True(resumed.Tables.Single(t => t.Table == "FlowRuns").SkippedAsComplete);
    }

    [Fact]
    public async Task Verify_only_names_the_table_that_differs()
    {
        // Arrange
        var (sourceCs, runId) = await SourceWithOneRunAsync(FlowStorageProvider.PostgreSql);
        var targetCs = await _db.CreateSqlServerDatabaseAsync();
        var options = Options(FlowStorageProvider.PostgreSql, sourceCs, FlowStorageProvider.SqlServer, targetCs);
        await new FlowStorageCopier(options).CopyAsync();
        await using (var conn = new SqlConnection(targetCs))
            await conn.ExecuteAsync("UPDATE FlowSteps SET OutputJson = N'{\"tampered\":true}' WHERE RunId = @RunId AND StepKey = 'b'", new { RunId = runId });

        // Act
        var ex = await Assert.ThrowsAsync<FlowStorageVerificationException>(() => new FlowStorageCopier(options).VerifyAsync());

        // Assert
        Assert.Contains("FlowSteps", ex.Message);
        Assert.False(ex.Result.Tables.Single(t => t.Table == "FlowSteps").Verified);
        Assert.True(ex.Result.Tables.Single(t => t.Table == "FlowRuns").Verified);
        Assert.Equal(0, ex.Result.TotalCopiedRows);
    }

    [Fact]
    public async Task A_value_PostgreSQL_cannot_store_fails_with_the_row_named_and_rolls_that_table_back()
    {
        // Arrange — SQL Server NVARCHAR holds U+0000; PostgreSQL text does not.
        var (sourceCs, runId) = await SourceWithOneRunAsync(FlowStorageProvider.SqlServer, outputJson: "{\"v\":\"a\0b\"}");
        var targetCs = await _db.CreatePostgreSqlDatabaseAsync();
        var copier = new FlowStorageCopier(Options(FlowStorageProvider.SqlServer, sourceCs, FlowStorageProvider.PostgreSql, targetCs));

        // Act
        var ex = await Assert.ThrowsAsync<FlowStorageCopyException>(() => copier.CopyAsync());

        // Assert
        Assert.Equal("FlowSteps", ex.Table);
        Assert.Contains($"RunId={runId}, StepKey=b", ex.Message);
        Assert.Contains("column OutputJson", ex.Message);
        await using var conn = new NpgsqlConnection(targetCs);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM flow_steps"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM flow_runs"));
    }

    [Fact]
    public async Task The_legacy_event_table_is_copied_when_the_source_has_it_and_its_sequence_moves_on()
    {
        // Arrange — a database upgraded from 1.34 keeps FlowEvents (2.0 neither writes nor drops it).
        var (sourceCs, runId) = await SourceWithOneRunAsync(FlowStorageProvider.SqlServer);
        await using (var conn = new SqlConnection(sourceCs))
        {
            await conn.ExecuteAsync(Schema.StorageCatalog.SqlLegacyEventsDdl);
            await conn.ExecuteAsync(
                "INSERT INTO FlowEvents (RunId, [Type], StepKey, [Message]) VALUES (@RunId, 'run.started', NULL, NULL), (@RunId, 'step.completed', 'a', N'ok ✓'), (@RunId, 'run.completed', NULL, N'done 😀')",
                new { RunId = runId });
        }

        var targetCs = await _db.CreatePostgreSqlDatabaseAsync();

        // Act
        var result = await new FlowStorageCopier(Options(FlowStorageProvider.SqlServer, sourceCs, FlowStorageProvider.PostgreSql, targetCs)).CopyAsync();

        // Assert
        Assert.True(result.IsVerified);
        Assert.Equal(3, result.Tables.Single(t => t.Table == "FlowEvents").CopiedRows);
        await using var pg = new NpgsqlConnection(targetCs);
        Assert.Equal("done 😀", await pg.ExecuteScalarAsync<string>("SELECT message FROM flow_events WHERE sequence = 3"));
        Assert.Equal(4, await pg.ExecuteScalarAsync<long>("INSERT INTO flow_events (run_id, type) VALUES (@RunId, 'later') RETURNING sequence", new { RunId = runId }));
    }

    [Fact]
    public async Task Skip_legacy_events_leaves_the_legacy_table_behind()
    {
        // Arrange
        var (sourceCs, _) = await SourceWithOneRunAsync(FlowStorageProvider.PostgreSql);
        await using (var conn = new NpgsqlConnection(sourceCs))
            await conn.ExecuteAsync(Schema.StorageCatalog.PgLegacyEventsDdl);
        var targetCs = await _db.CreatePostgreSqlDatabaseAsync();
        var options = Options(FlowStorageProvider.PostgreSql, sourceCs, FlowStorageProvider.PostgreSql, targetCs);
        options.SkipLegacyEvents = true;

        // Act
        var result = await new FlowStorageCopier(options).CopyAsync();

        // Assert
        Assert.True(result.IsVerified);
        Assert.DoesNotContain(result.Tables, t => t.Table == "FlowEvents");
        await using var pg = new NpgsqlConnection(targetCs);
        Assert.False(await pg.ExecuteScalarAsync<bool>("SELECT to_regclass('flow_events') IS NOT NULL"));
    }
}
