using System.Text;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.Internal;

/// <summary>
/// Builds the explicit SQL the copier issues on each backend: keyset-ordered batch reads, filtered counts and
/// the column lists of the bulk loads. Pure string building, unit-tested per dialect.
/// </summary>
/// <remarks>
/// Batches walk the primary key: <c>WHERE key &gt; @last ORDER BY key</c>, a clustered-index (SQL Server) or
/// btree (PostgreSQL) range seek per batch, so a table is never loaded whole and the cost per batch does not
/// grow with the offset. PostgreSQL compares row values directly; SQL Server has no row-value comparison, so
/// the predicate is expanded and prefixed with <c>K0 &gt;= @k0</c> to keep the seek on the leading key column.
/// Parameters: <c>@take</c>, <c>@cutoff</c> (expiry filter) and <c>@k0</c>..<c>@kN</c> (the last row's key).
/// </remarks>
internal static class StorageSql
{
    /// <summary>Quotes <paramref name="name"/> for <paramref name="provider"/>.</summary>
    public static string Quote(FlowStorageProvider provider, string name) =>
        provider == FlowStorageProvider.SqlServer ? "[" + name + "]" : "\"" + name + "\"";

    /// <summary>The comma-separated, quoted column list of <paramref name="table"/> on <paramref name="provider"/>.</summary>
    public static string ColumnList(FlowStorageProvider provider, StorageTable table)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Quote(provider, table.Columns[i].NameOn(provider)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The batch read: the next <c>@take</c> rows after the cursor (or from the start), in key order, honouring
    /// the table's expiry filter.
    /// </summary>
    /// <param name="provider">The backend the query runs on.</param>
    /// <param name="table">The table.</param>
    /// <param name="afterCursor">Whether a previous batch's last key is bound to <c>@k0</c>..<c>@kN</c>.</param>
    public static string SelectBatch(FlowStorageProvider provider, StorageTable table, bool afterCursor)
    {
        var name = Quote(provider, table.NameOn(provider));
        var where = new List<string>(2);
        if (table.ExpiryOrdinal >= 0)
            where.Add(Quote(provider, table.Columns[table.ExpiryOrdinal].NameOn(provider)) + " >= @cutoff");
        if (afterCursor)
            where.Add(KeysetPredicate(provider, table));

        var whereSql = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);
        var orderBy = " ORDER BY " + KeyList(provider, table);
        return provider == FlowStorageProvider.SqlServer
            ? "SELECT TOP (@take) " + ColumnList(provider, table) + " FROM " + name + whereSql + orderBy
            : "SELECT " + ColumnList(provider, table) + " FROM " + name + whereSql + orderBy + " LIMIT @take";
    }

    /// <summary>Counts the rows of <paramref name="table"/>, honouring its expiry filter.</summary>
    public static string Count(FlowStorageProvider provider, StorageTable table)
    {
        var name = Quote(provider, table.NameOn(provider));
        var where = table.ExpiryOrdinal >= 0
            ? " WHERE " + Quote(provider, table.Columns[table.ExpiryOrdinal].NameOn(provider)) + " >= @cutoff"
            : string.Empty;
        return provider == FlowStorageProvider.SqlServer
            ? "SELECT COUNT_BIG(*) FROM " + name + where
            : "SELECT COUNT(*) FROM " + name + where;
    }

    /// <summary>1 when <paramref name="tableName"/> has at least one row (any row, no filter), else 0.</summary>
    public static string AnyRow(FlowStorageProvider provider, string tableName) =>
        "SELECT CASE WHEN EXISTS (SELECT 1 FROM " + Quote(provider, tableName) + ") THEN 1 ELSE 0 END";

    /// <summary>
    /// Counts runs in status <c>Running</c>, case-insensitively (the engine writes the canonical spelling; older
    /// rows or hand edits may not), and lists up to five of their ids.
    /// </summary>
    public static string ActiveRuns(FlowStorageProvider provider) => provider == FlowStorageProvider.SqlServer
        ? "SELECT COUNT_BIG(*) FROM [FlowRuns] WHERE UPPER(LTRIM(RTRIM([Status]))) = 'RUNNING'; "
          + "SELECT TOP (5) [Id] FROM [FlowRuns] WHERE UPPER(LTRIM(RTRIM([Status]))) = 'RUNNING' ORDER BY [StartedAt]"
        : "SELECT COUNT(*) FROM \"flow_runs\" WHERE upper(btrim(\"status\")) = 'RUNNING'; "
          + "SELECT \"id\" FROM \"flow_runs\" WHERE upper(btrim(\"status\")) = 'RUNNING' ORDER BY \"started_at\" LIMIT 5";

    /// <summary>Every column of every base table in the connection's default schema.</summary>
    public static string SchemaColumns(FlowStorageProvider provider) => provider == FlowStorageProvider.SqlServer
        ? """
          SELECT c.TABLE_NAME, c.COLUMN_NAME
          FROM INFORMATION_SCHEMA.COLUMNS AS c
          JOIN INFORMATION_SCHEMA.TABLES AS t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
          WHERE c.TABLE_SCHEMA = SCHEMA_NAME() AND t.TABLE_TYPE = 'BASE TABLE'
          """
        : """
          SELECT c.table_name, c.column_name
          FROM information_schema.columns AS c
          JOIN information_schema.tables AS t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
          WHERE c.table_schema = current_schema() AND t.table_type = 'BASE TABLE'
          """;

    /// <summary>Moves the identity / sequence of <paramref name="table"/> past the copied maximum.</summary>
    public static string ReseedIdentity(FlowStorageProvider provider, StorageTable table)
    {
        var column = table.Columns[table.IdentityOrdinal];
        return provider == FlowStorageProvider.SqlServer
            ? "DBCC CHECKIDENT ('" + table.SqlName + "', RESEED) WITH NO_INFOMSGS"
            : "SELECT setval(pg_get_serial_sequence('" + table.PgName + "', '" + column.PgName + "'), "
              + "(SELECT MAX(" + Quote(provider, column.PgName) + ") FROM " + Quote(provider, table.PgName) + "))";
    }

    /// <summary>Comma-separated quoted key columns, in key order.</summary>
    internal static string KeyList(FlowStorageProvider provider, StorageTable table)
    {
        var sb = new StringBuilder();
        for (var k = 0; k < table.KeyOrdinals.Length; k++)
        {
            if (k > 0) sb.Append(", ");
            sb.Append(Quote(provider, table.Columns[table.KeyOrdinals[k]].NameOn(provider)));
        }

        return sb.ToString();
    }

    /// <summary>"Strictly after the cursor" in key order.</summary>
    internal static string KeysetPredicate(FlowStorageProvider provider, StorageTable table)
    {
        var keys = table.KeyOrdinals;
        if (provider == FlowStorageProvider.PostgreSql)
        {
            if (keys.Length == 1)
                return Quote(provider, table.Columns[keys[0]].PgName) + " > @k0";
            var parameters = new string[keys.Length];
            for (var k = 0; k < keys.Length; k++) parameters[k] = "@k" + k;
            return "(" + KeyList(provider, table) + ") > (" + string.Join(", ", parameters) + ")";
        }

        var expanded = ExpandSqlServer(table, 0);
        if (keys.Length == 1)
            return expanded;
        return Quote(provider, table.Columns[keys[0]].SqlName) + " >= @k0 AND (" + expanded + ")";
    }

    private static string ExpandSqlServer(StorageTable table, int k)
    {
        var column = "[" + table.Columns[table.KeyOrdinals[k]].SqlName + "]";
        if (k == table.KeyOrdinals.Length - 1)
            return column + " > @k" + k;
        return column + " > @k" + k + " OR (" + column + " = @k" + k + " AND (" + ExpandSqlServer(table, k + 1) + "))";
    }
}
