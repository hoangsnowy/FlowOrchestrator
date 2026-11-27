using System.Reflection;
using System.Text.RegularExpressions;
using FlowOrchestrator.PostgreSQL;
using FlowOrchestrator.SqlServer;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.UnitTests;

/// <summary>
/// The column map is the contract of the copy: a column the map does not know is a column the copy drops.
/// These tests parse the DDL the two 2.0 migrators actually issue and require the map to match it exactly —
/// table set, column set, value kind and declared length — so a schema change that forgets the copy tool
/// fails here instead of in a customer's migration.
/// </summary>
public sealed class StorageCatalogTests
{
    // GetValue, not GetRawConstantValue: the SQL is a const on SQL Server but a static readonly (built with the
    // EnsureIndex / EnsureColumn guards) on PostgreSQL; GetValue reads either.
    private static string MigrationSqlOf<T>() =>
        (string)typeof(T).GetField("MigrationSql", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static readonly Regex SqlCreateTable = new(@"CREATE TABLE \[(\w+)\] \((.*?)\n\s*\);", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex SqlColumn = new(@"^\s*\[(\w+)\]\s+(UNIQUEIDENTIFIER|NVARCHAR|BIT|INT|BIGINT|FLOAT|DATETIMEOFFSET)(?:\((\d+|MAX)\))?", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex SqlAlterAdd = new(@"ALTER TABLE \[(\w+)\] ADD \[(\w+)\] (UNIQUEIDENTIFIER|NVARCHAR|BIT|INT|BIGINT|FLOAT|DATETIMEOFFSET)(?:\((\d+|MAX)\))?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex PgCreateTable = new(@"CREATE TABLE IF NOT EXISTS (\w+) \((.*?)\n\s*\);", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex PgColumn = new(@"^\s*(\w+)\s+(UUID|VARCHAR|TEXT|BOOLEAN|INT|BIGINT|BIGSERIAL|DOUBLE PRECISION|TIMESTAMPTZ)(?:\((\d+)\))?", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex PgAlterAdd = new(@"ALTER TABLE (\w+) ADD COLUMN IF NOT EXISTS (\w+) (UUID|VARCHAR|TEXT|BOOLEAN|INT|BIGINT|DOUBLE PRECISION|TIMESTAMPTZ)(?:\((\d+)\))?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static Dictionary<string, SortedSet<string>> ParseSqlServer(string ddl)
    {
        var tables = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (Match table in SqlCreateTable.Matches(ddl))
        {
            var columns = tables[table.Groups[1].Value] = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match column in SqlColumn.Matches(table.Groups[2].Value))
                columns.Add(Describe(column.Groups[1].Value, SqlKind(column.Groups[2].Value), SqlLength(column.Groups[3].Value)));
        }

        foreach (Match alter in SqlAlterAdd.Matches(ddl))
            tables[alter.Groups[1].Value].Add(Describe(alter.Groups[2].Value, SqlKind(alter.Groups[3].Value), SqlLength(alter.Groups[4].Value)));
        return tables;
    }

    private static Dictionary<string, SortedSet<string>> ParsePostgres(string ddl)
    {
        var tables = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (Match table in PgCreateTable.Matches(ddl))
        {
            var columns = tables[table.Groups[1].Value] = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var column in PgColumn.Matches(table.Groups[2].Value).Where(c => c.Groups[1].Value != "PRIMARY"))
            {
                columns.Add(Describe(column.Groups[1].Value, PgKind(column.Groups[2].Value), PgLength(column.Groups[2].Value, column.Groups[3].Value)));
            }
        }

        foreach (Match alter in PgAlterAdd.Matches(ddl))
            tables[alter.Groups[1].Value].Add(Describe(alter.Groups[2].Value, PgKind(alter.Groups[3].Value), PgLength(alter.Groups[3].Value, alter.Groups[4].Value)));
        return tables;
    }

    private static string Describe(string name, StorageColumnKind kind, int? length) => $"{name}:{kind}:{length?.ToString() ?? "max"}";

    private static StorageColumnKind SqlKind(string type) => type switch
    {
        "UNIQUEIDENTIFIER" => StorageColumnKind.Guid,
        "NVARCHAR" => StorageColumnKind.String,
        "BIT" => StorageColumnKind.Boolean,
        "INT" => StorageColumnKind.Int32,
        "BIGINT" => StorageColumnKind.Int64,
        "FLOAT" => StorageColumnKind.Double,
        "DATETIMEOFFSET" => StorageColumnKind.DateTimeOffset,
        _ => throw new InvalidOperationException(type),
    };

    private static int? SqlLength(string length) => length is "" or "MAX" ? null : int.Parse(length);

    private static StorageColumnKind PgKind(string type) => type switch
    {
        "UUID" => StorageColumnKind.Guid,
        "VARCHAR" or "TEXT" => StorageColumnKind.String,
        "BOOLEAN" => StorageColumnKind.Boolean,
        "INT" => StorageColumnKind.Int32,
        "BIGINT" or "BIGSERIAL" => StorageColumnKind.Int64,
        "DOUBLE PRECISION" => StorageColumnKind.Double,
        "TIMESTAMPTZ" => StorageColumnKind.DateTimeOffset,
        _ => throw new InvalidOperationException(type),
    };

    private static int? PgLength(string type, string length) => type == "VARCHAR" ? int.Parse(length) : null;

    private static Dictionary<string, SortedSet<string>> CatalogOf(FlowStorageProvider provider)
    {
        var tables = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var table in StorageCatalog.CoreTables)
        {
            var columns = tables[table.NameOn(provider)] = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                // PostgreSQL VARCHAR always has a length here; SQL Server NVARCHAR(MAX) / PostgreSQL TEXT are "max".
                columns.Add(Describe(column.NameOn(provider), column.Kind, column.Kind == StorageColumnKind.String ? column.MaxLength : null));
            }
        }

        return tables;
    }

    [Fact]
    public void The_map_matches_every_table_and_column_the_SQL_Server_migrator_creates()
    {
        // Arrange
        var ddl = ParseSqlServer(MigrationSqlOf<FlowOrchestratorSqlMigrator>());

        // Act
        var catalog = CatalogOf(FlowStorageProvider.SqlServer);

        // Assert
        Assert.Equal(ddl.Keys.Order(StringComparer.Ordinal), catalog.Keys.Order(StringComparer.Ordinal));
        foreach (var (table, columns) in ddl)
            Assert.Equal(columns, catalog[table]);
    }

    [Fact]
    public void The_map_matches_every_table_and_column_the_PostgreSQL_migrator_creates()
    {
        // Arrange
        var ddl = ParsePostgres(MigrationSqlOf<PostgreSqlFlowOrchestratorMigrator>());

        // Act
        var catalog = CatalogOf(FlowStorageProvider.PostgreSql);

        // Assert
        Assert.Equal(ddl.Keys.Order(StringComparer.Ordinal), catalog.Keys.Order(StringComparer.Ordinal));
        foreach (var (table, columns) in ddl)
            Assert.Equal(columns, catalog[table]);
    }

    [Fact]
    public void The_legacy_event_table_matches_the_1_34_DDL_on_both_backends()
    {
        // Arrange
        var sql = ParseSqlServer(StorageCatalog.SqlLegacyEventsDdl);
        var pg = ParsePostgres(StorageCatalog.PgLegacyEventsDdl);
        var legacy = StorageCatalog.LegacyFlowEvents;

        // Act
        var sqlColumns = legacy.Columns.Select(c => Describe(c.SqlName, c.Kind, c.Kind == StorageColumnKind.String ? c.MaxLength : null)).ToHashSet();
        var pgColumns = legacy.Columns.Select(c => Describe(c.PgName, c.Kind, c.Kind == StorageColumnKind.String ? c.MaxLength : null)).ToHashSet();

        // Assert
        Assert.True(sql["FlowEvents"].SetEquals(sqlColumns));
        Assert.True(pg["flow_events"].SetEquals(pgColumns));
    }

    [Fact]
    public void Every_PostgreSQL_name_is_the_snake_case_of_the_SQL_Server_name()
    {
        // Arrange
        static string Snake(string pascal) => Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])", "_$1", RegexOptions.None, TimeSpan.FromSeconds(1)).ToLowerInvariant();

        // Act
        var mismatches = new List<string>();
        foreach (var table in StorageCatalog.CoreTables)
        {
            if (Snake(table.SqlName) != table.PgName)
                mismatches.Add(table.SqlName);
            mismatches.AddRange(table.Columns
                .Where(column => Snake(column.SqlName) != column.PgName)
                .Select(column => table.SqlName + "." + column.SqlName));
        }

        // Assert
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Keys_are_the_primary_keys_the_migrators_declare()
    {
        // Arrange
        var expected = new Dictionary<string, string>
        {
            ["FlowDefinitions"] = "Id",
            ["FlowScheduleStates"] = "JobId",
            ["FlowRuns"] = "Id",
            ["FlowRunControls"] = "RunId",
            ["FlowIdempotencyKeys"] = "FlowId,TriggerKey,IdempotencyKey",
            ["FlowSteps"] = "RunId,StepKey",
            ["FlowStepAttempts"] = "RunId,StepKey,AttemptNo",
            ["FlowOutputs"] = "RunId,Key",
            ["FlowStepDispatches"] = "RunId,StepKey",
            ["FlowStepClaims"] = "RunId,StepKey",
            ["FlowStepLeases"] = "RunId,StepKey",
            ["FlowSignalWaiters"] = "RunId,StepKey",
            ["WebhookReplayNonces"] = "FlowId,TriggerKey,Nonce",
            ["WebhookRejections"] = "Id",
        };

        // Act
        var actual = StorageCatalog.CoreTables.ToDictionary(
            t => t.SqlName, t => string.Join(',', t.KeyOrdinals.Select(o => t.Columns[o].SqlName)));

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Only_the_webhook_rejection_log_has_an_identity_and_only_nonces_expire()
    {
        // Arrange
        var tables = StorageCatalog.CoreTables;

        // Act
        var identity = tables.Where(t => t.IdentityOrdinal >= 0).Select(t => t.SqlName).ToArray();
        var expiring = tables.Where(t => t.ExpiryOrdinal >= 0).Select(t => t.SqlName + "." + t.Columns[t.ExpiryOrdinal].SqlName).ToArray();

        // Assert
        Assert.Equal(["WebhookRejections"], identity);
        Assert.Equal(["WebhookReplayNonces.ExpiresAt"], expiring);
    }
}
