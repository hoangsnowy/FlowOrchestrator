using FlowOrchestrator.StorageMigration.Internal;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.UnitTests;

/// <summary>
/// Pins the statements the copier issues: keyset batch reads (row-value comparison on PostgreSQL, an expanded
/// predicate with a leading seek column on SQL Server), the expiry filter, counts and identity re-seeding.
/// Pure text checks.
/// </summary>
public sealed class StorageSqlTests
{
    [Fact]
    public void SQL_Server_first_batch_reads_in_key_order_without_a_cursor()
    {
        // Arrange
        var table = StorageCatalog.FlowRuns;

        // Act
        var sql = StorageSql.SelectBatch(FlowStorageProvider.SqlServer, table, afterCursor: false);

        // Assert — computed columns (IdText, RunSearchText) are not read.
        Assert.Equal(
            "SELECT TOP (@take) [Id], [FlowId], [FlowName], [Status], [TriggerKey], [TriggerDataJson], [BackgroundJobId], [StartedAt], [CompletedAt], [SourceRunId] FROM [FlowRuns] ORDER BY [Id]",
            sql);
    }

    [Fact]
    public void SQL_Server_composite_keyset_expands_the_comparison_and_keeps_a_seek_on_the_leading_column()
    {
        // Arrange
        var table = StorageCatalog.FlowStepAttempts;

        // Act
        var predicate = StorageSql.KeysetPredicate(FlowStorageProvider.SqlServer, table);

        // Assert
        Assert.Equal(
            "[RunId] >= @k0 AND ([RunId] > @k0 OR ([RunId] = @k0 AND ([StepKey] > @k1 OR ([StepKey] = @k1 AND ([AttemptNo] > @k2)))))",
            predicate);
    }

    [Fact]
    public void PostgreSQL_composite_keyset_uses_a_row_value_comparison()
    {
        // Arrange
        var table = StorageCatalog.FlowSteps;

        // Act
        var sql = StorageSql.SelectBatch(FlowStorageProvider.PostgreSql, table, afterCursor: true);

        // Assert
        Assert.EndsWith(
            " FROM \"flow_steps\" WHERE (\"run_id\", \"step_key\") > (@k0, @k1) ORDER BY \"run_id\", \"step_key\" LIMIT @take",
            sql);
        Assert.StartsWith("SELECT \"run_id\", \"step_key\", \"step_type\"", sql);
    }

    [Fact]
    public void Single_column_keysets_compare_the_column_directly()
    {
        // Arrange
        var table = StorageCatalog.WebhookRejections;

        // Act
        var sqlServer = StorageSql.KeysetPredicate(FlowStorageProvider.SqlServer, table);
        var postgres = StorageSql.KeysetPredicate(FlowStorageProvider.PostgreSql, table);

        // Assert
        Assert.Equal("[Id] > @k0", sqlServer);
        Assert.Equal("\"id\" > @k0", postgres);
    }

    [Fact]
    public void Replay_nonces_are_filtered_to_the_unexpired_ones_in_reads_and_counts()
    {
        // Arrange
        var table = StorageCatalog.WebhookReplayNonces;

        // Act
        var read = StorageSql.SelectBatch(FlowStorageProvider.SqlServer, table, afterCursor: true);
        var count = StorageSql.Count(FlowStorageProvider.PostgreSql, table);

        // Assert
        Assert.Contains("WHERE [ExpiresAt] >= @cutoff AND [FlowId] >= @k0 AND (", read);
        Assert.Equal("SELECT COUNT(*) FROM \"webhook_replay_nonces\" WHERE \"expires_at\" >= @cutoff", count);
    }

    [Fact]
    public void Tables_without_a_filter_count_every_row()
    {
        // Arrange
        var table = StorageCatalog.FlowOutputs;

        // Act
        var sql = StorageSql.Count(FlowStorageProvider.SqlServer, table);

        // Assert
        Assert.Equal("SELECT COUNT_BIG(*) FROM [FlowOutputs]", sql);
    }

    [Fact]
    public void Identity_reseed_moves_the_generator_past_the_copied_maximum()
    {
        // Arrange
        var table = StorageCatalog.WebhookRejections;

        // Act
        var sqlServer = StorageSql.ReseedIdentity(FlowStorageProvider.SqlServer, table);
        var postgres = StorageSql.ReseedIdentity(FlowStorageProvider.PostgreSql, table);

        // Assert
        Assert.Equal("DBCC CHECKIDENT ('WebhookRejections', RESEED) WITH NO_INFOMSGS", sqlServer);
        Assert.Equal("SELECT setval(pg_get_serial_sequence('webhook_rejections', 'id'), (SELECT MAX(\"id\") FROM \"webhook_rejections\"))", postgres);
    }

    [Fact]
    public void The_reserved_word_key_is_quoted_on_both_backends()
    {
        // Arrange
        var table = StorageCatalog.FlowOutputs;

        // Act
        var sqlServer = StorageSql.ColumnList(FlowStorageProvider.SqlServer, table);
        var postgres = StorageSql.ColumnList(FlowStorageProvider.PostgreSql, table);

        // Assert
        Assert.Equal("[RunId], [Key], [ValueJson], [CreatedAt]", sqlServer);
        Assert.Equal("\"run_id\", \"key\", \"value_json\", \"created_at\"", postgres);
    }

    [Fact]
    public void Active_runs_are_matched_case_insensitively_on_both_backends()
    {
        // Arrange
        var providers = new[] { FlowStorageProvider.SqlServer, FlowStorageProvider.PostgreSql };

        // Act
        var statements = providers.Select(StorageSql.ActiveRuns).ToArray();

        // Assert
        Assert.Contains("UPPER(LTRIM(RTRIM([Status]))) = 'RUNNING'", statements[0]);
        Assert.Contains("upper(btrim(\"status\")) = 'RUNNING'", statements[1]);
    }
}
