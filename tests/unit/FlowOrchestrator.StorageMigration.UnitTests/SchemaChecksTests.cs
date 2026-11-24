using FlowOrchestrator.StorageMigration.Internal;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.UnitTests;

/// <summary>
/// The schema comparison behind the "source too old", "target incomplete" and "schema newer than the tool"
/// refusals.
/// </summary>
public sealed class SchemaChecksTests
{
    private static SchemaSnapshot Complete(FlowStorageProvider provider, params (string Table, string Column)[] extra)
    {
        var rows = new List<(string, string)>();
        foreach (var table in StorageCatalog.CoreTables)
        {
            foreach (var column in table.Columns)
                rows.Add((table.NameOn(provider), column.NameOn(provider)));
        }

        rows.AddRange(extra);
        return SchemaSnapshot.From(rows);
    }

    [Fact]
    public void A_complete_2_0_schema_has_nothing_missing_and_nothing_unknown()
    {
        // Arrange
        var schema = Complete(FlowStorageProvider.PostgreSql);

        // Act
        var missing = SchemaChecks.FindMissing(schema, FlowStorageProvider.PostgreSql, StorageCatalog.CoreTables);
        var unknown = SchemaChecks.FindUnknownColumns(schema, FlowStorageProvider.PostgreSql, StorageCatalog.CoreTables);

        // Assert
        Assert.Empty(missing);
        Assert.Empty(unknown);
    }

    [Fact]
    public void A_pre_2_0_schema_reports_the_missing_column_and_table()
    {
        // Arrange — a 1.x database: no SourceRunId yet, no webhook tables.
        var rows = new List<(string, string)>();
        foreach (var table in StorageCatalog.CoreTables)
        {
            if (table.SqlName is "WebhookReplayNonces" or "WebhookRejections")
                continue;
            foreach (var column in table.Columns)
            {
                if (table.SqlName == "FlowRuns" && column.SqlName == "SourceRunId")
                    continue;
                rows.Add((table.SqlName, column.SqlName));
            }
        }

        var schema = SchemaSnapshot.From(rows);

        // Act
        var missing = SchemaChecks.FindMissing(schema, FlowStorageProvider.SqlServer, StorageCatalog.CoreTables);

        // Assert
        Assert.Equal(["FlowRuns.SourceRunId", "WebhookReplayNonces", "WebhookRejections"], missing);
    }

    [Fact]
    public void SQL_Server_run_search_computed_columns_are_known_and_ignored()
    {
        // Arrange
        var schema = Complete(FlowStorageProvider.SqlServer, ("FlowRuns", "IdText"), ("FlowRuns", "RunSearchText"));

        // Act
        var unknown = SchemaChecks.FindUnknownColumns(schema, FlowStorageProvider.SqlServer, StorageCatalog.CoreTables);

        // Assert
        Assert.Empty(unknown);
    }

    [Fact]
    public void A_column_from_a_newer_FlowOrchestrator_is_reported_so_it_is_not_dropped_silently()
    {
        // Arrange
        var schema = Complete(FlowStorageProvider.PostgreSql, ("flow_steps", "retry_policy_json"));

        // Act
        var unknown = SchemaChecks.FindUnknownColumns(schema, FlowStorageProvider.PostgreSql, StorageCatalog.CoreTables);

        // Assert
        Assert.Equal(["flow_steps.retry_policy_json"], unknown);
    }

    [Fact]
    public void Table_and_column_names_compare_case_insensitively()
    {
        // Arrange
        var schema = SchemaSnapshot.From([("flowruns", "ID")]);

        // Act
        var hasTable = schema.HasTable("FlowRuns");
        var columns = schema.ColumnsOf("FLOWRUNS");

        // Assert
        Assert.True(hasTable);
        Assert.NotNull(columns);
        Assert.Contains("Id", columns);
    }
}
