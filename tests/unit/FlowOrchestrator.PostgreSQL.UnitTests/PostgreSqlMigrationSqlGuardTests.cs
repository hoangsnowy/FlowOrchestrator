namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Pins that every <c>CREATE INDEX</c> and <c>ALTER TABLE</c> in the PostgreSQL start-up migration runs behind
/// a catalog guard (<c>EnsureIndex</c> / <c>EnsureColumn</c>). PostgreSQL takes the table lock of
/// <c>CREATE INDEX IF NOT EXISTS</c> and <c>ADD COLUMN IF NOT EXISTS</c> before it checks the name, so one
/// unguarded statement makes every start queue behind running writers and stall the writers queued behind it.
/// </summary>
/// <remarks>
/// A text check, so it fails in the unit suite in milliseconds. The integration test
/// <c>Second_start_on_an_up_to_date_schema_completes_while_another_session_holds_write_locks</c> proves the
/// behaviour against a real server, but only after a 30 s budget.
/// </remarks>
public sealed class PostgreSqlMigrationSqlGuardTests
{
    /// <summary>Every SQL batch the migrator runs at start-up, by name.</summary>
    public static TheoryData<string, string> StartupSql => new()
    {
        { nameof(PostgreSqlFlowOrchestratorMigrator.MigrationSql), PostgreSqlFlowOrchestratorMigrator.MigrationSql },
        { nameof(PostgreSqlFlowOrchestratorMigrator.TrigramSql), PostgreSqlFlowOrchestratorMigrator.TrigramSql },
    };

    /// <summary>Rejects any index or column statement that is not the body of a catalog guard.</summary>
    /// <param name="name">Name of the SQL batch, for the failure message.</param>
    /// <param name="sql">The SQL batch.</param>
    [Theory]
    [MemberData(nameof(StartupSql))]
    public void Every_index_and_column_statement_runs_behind_a_catalog_guard(string name, string sql)
    {
        // Arrange
        var lines = sql.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
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

    /// <summary>
    /// Pins the number of guarded statements, so a statement dropped from the batch (rather than guarded) is
    /// noticed: 16 indexes and 3 columns in <c>MigrationSql</c>, 5 trigram indexes in <c>TrigramSql</c>.
    /// </summary>
    [Fact]
    public void Startup_sql_still_creates_every_index_and_column_of_the_schema()
    {
        // Arrange
        var migration = PostgreSqlFlowOrchestratorMigrator.MigrationSql;
        var trigram = PostgreSqlFlowOrchestratorMigrator.TrigramSql;

        // Act
        var migrationIndexes = Count(migration, "CREATE INDEX IF NOT EXISTS ");
        var migrationColumns = Count(migration, "ADD COLUMN IF NOT EXISTS ");
        var trigramIndexes = Count(trigram, "CREATE INDEX IF NOT EXISTS ");

        // Assert
        Assert.Equal(16, migrationIndexes);
        Assert.Equal(3, migrationColumns);
        Assert.Equal(5, trigramIndexes);
    }

    private static int Count(string sql, string needle)
    {
        var count = 0;
        for (var i = sql.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = sql.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static bool IsTableLockingDdl(string line) =>
        line.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase);
}
