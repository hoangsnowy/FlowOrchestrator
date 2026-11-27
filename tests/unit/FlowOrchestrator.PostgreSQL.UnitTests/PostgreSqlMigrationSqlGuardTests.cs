namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Pins that every <c>CREATE INDEX</c> and <c>ALTER TABLE</c> in the PostgreSQL start-up migration runs behind
/// a catalog guard (<c>EnsureIndex</c> / <c>EnsureColumn</c>). PostgreSQL takes the table lock of
/// <c>CREATE INDEX IF NOT EXISTS</c> and <c>ADD COLUMN IF NOT EXISTS</c> before it checks the name, so one
/// unguarded statement makes every start queue behind running writers and stall the writers queued behind it.
/// </summary>
/// <remarks>
/// A text check, so it fails in the unit suite in seconds. The integration test
/// <c>Migration_rerun_on_an_up_to_date_schema_requests_no_lock_a_running_host_could_be_holding</c> proves the
/// behaviour, but only after a 30 s timeout. A merge once brought back a plain
/// <c>CREATE INDEX IF NOT EXISTS ix_flow_step_dispatches_unsent</c>, and only that integration test caught it.
/// </remarks>
public sealed class PostgreSqlMigrationSqlGuardTests
{
    public static TheoryData<string, string> StartupSql => new()
    {
        { nameof(PostgreSqlFlowOrchestratorMigrator.MigrationSql), PostgreSqlFlowOrchestratorMigrator.MigrationSql },
        { nameof(PostgreSqlFlowOrchestratorMigrator.TrigramSql), PostgreSqlFlowOrchestratorMigrator.TrigramSql },
    };

    [Theory]
    [MemberData(nameof(StartupSql))]
    public void Every_index_and_column_statement_runs_behind_a_catalog_guard(string name, string sql)
    {
        // Arrange
        var lines = sql.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var unguarded = new List<string>();
        var ddl = 0;

        // Act — a guarded statement is the body of an IF ... THEN emitted by EnsureIndex / EnsureColumn.
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsTableLockingDdl(lines[i]))
                continue;
            ddl++;
            if (i == 0 || !lines[i - 1].EndsWith("THEN", StringComparison.Ordinal))
                unguarded.Add(lines[i]);
        }

        // Assert
        Assert.True(ddl > 0, $"{name} has no index or column statement; the check would prove nothing.");
        Assert.True(unguarded.Count == 0, $"{name} has unguarded DDL, use EnsureIndex / EnsureColumn: " + string.Join(" | ", unguarded));
    }

    private static bool IsTableLockingDdl(string line) =>
        line.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(StartupSql))]
    public void The_open_step_index_is_never_built_in_the_startup_batch(string name, string sql)
    {
        // Arrange - #189 P0-1: a plain CREATE INDEX on flow_steps holds SHARE, blocking every step write, for the
        // whole build of an upgraded table. It belongs to the background CONCURRENTLY path only.

        // Act
        var mentions = sql.Contains("ix_flow_steps_run_id_open", StringComparison.Ordinal);

        // Assert
        Assert.False(mentions, $"{name} builds ix_flow_steps_run_id_open; build it CONCURRENTLY in the background.");
    }

    [Fact]
    public void The_open_step_index_is_built_concurrently_with_the_store_s_predicate()
    {
        // Arrange
        var ddl = PostgreSqlFlowOrchestratorMigrator.CreateOpenStepIndexSql;

        // Act
        var concurrently = ddl.StartsWith("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_flow_steps_run_id_open ON flow_steps", StringComparison.Ordinal);

        // Assert
        Assert.True(concurrently, ddl);
        Assert.EndsWith("WHERE status IN ('Running', 'Pending')", ddl, StringComparison.Ordinal);
    }
}
