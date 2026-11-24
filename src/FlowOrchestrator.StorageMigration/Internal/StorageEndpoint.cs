using System.Data.Common;
using FlowOrchestrator.StorageMigration.Schema;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.StorageMigration.Internal;

/// <summary>The tables and columns a database has in its default schema, by case-insensitive name.</summary>
internal sealed class SchemaSnapshot
{
    private readonly Dictionary<string, HashSet<string>> _tables;

    public SchemaSnapshot(Dictionary<string, HashSet<string>> tables) => _tables = tables;

    /// <summary>Whether the table exists.</summary>
    public bool HasTable(string name) => _tables.ContainsKey(name);

    /// <summary>The table's columns, or <see langword="null"/> when it does not exist.</summary>
    public IReadOnlySet<string>? ColumnsOf(string name) => _tables.TryGetValue(name, out var columns) ? columns : null;

    /// <summary>Builds a snapshot from (table, column) pairs.</summary>
    public static SchemaSnapshot From(IEnumerable<(string Table, string Column)> rows)
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, column) in rows)
        {
            if (!tables.TryGetValue(table, out var columns))
                tables[table] = columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            columns.Add(column);
        }

        return new SchemaSnapshot(tables);
    }
}

/// <summary>Writes one table inside one target transaction.</summary>
internal interface ITableWriter : IAsyncDisposable
{
    /// <summary>Bulk-loads <paramref name="rows"/> (already prepared for this target).</summary>
    Task WriteAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken);

    /// <summary>Re-seeds the identity column when the table has one, then commits.</summary>
    Task CommitAsync(bool reseedIdentity, CancellationToken cancellationToken);
}

/// <summary>
/// One side of a copy. Reads use a single lazily-opened connection; each table write opens its own
/// connection and transaction.
/// </summary>
internal abstract class StorageEndpoint : IAsyncDisposable
{
    /// <summary>A batch is cut short once it holds this many characters of text (about 64 MB of UTF-16).</summary>
    internal const long MaxBatchChars = 32L * 1024 * 1024;

    private DbConnection? _connection;

    protected StorageEndpoint(FlowStorageProvider provider, string connectionString, int commandTimeoutSeconds)
    {
        Provider = provider;
        ConnectionString = connectionString;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>The backend.</summary>
    public FlowStorageProvider Provider { get; }

    /// <summary>The connection string.</summary>
    public string ConnectionString { get; }

    /// <summary>Command timeout for long-running statements.</summary>
    protected int CommandTimeoutSeconds { get; }

    /// <summary>A normalized server + database identity, to refuse copying a database onto itself.</summary>
    public abstract string Identity { get; }

    /// <summary>Server and database, for messages (never the credentials).</summary>
    public abstract string Display { get; }

    /// <summary>Creates an unopened connection.</summary>
    protected abstract DbConnection CreateConnection();

    /// <summary>Runs this provider's 2.0 migrator and waits for its background index builds.</summary>
    public abstract Task RunMigratorAsync(ILoggerFactory loggerFactory, CancellationToken cancellationToken);

    /// <summary>Starts writing <paramref name="table"/> in a new transaction.</summary>
    public abstract Task<ITableWriter> BeginTableAsync(StorageTable table, CancellationToken cancellationToken);

    /// <summary>Opens (once) and returns the read connection.</summary>
    protected async Task<DbConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            _connection = connection;
        }

        return _connection;
    }

    /// <summary>Executes a statement on the read connection.</summary>
    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the tables and columns of the default schema.</summary>
    public async Task<SchemaSnapshot> ReadSchemaAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = StorageSql.SchemaColumns(Provider);
        var rows = new List<(string, string)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return SchemaSnapshot.From(rows);
    }

    /// <summary>Counts the rows of <paramref name="table"/> that the copy covers.</summary>
    public async Task<long> CountAsync(StorageTable table, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = StorageSql.Count(Provider, table);
        cmd.CommandTimeout = CommandTimeoutSeconds;
        if (table.ExpiryOrdinal >= 0)
            AddParameter(cmd, "@cutoff", cutoff);
        var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Whether <paramref name="tableName"/> holds any row.</summary>
    public async Task<bool> HasRowsAsync(string tableName, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = StorageSql.AnyRow(Provider, tableName);
        cmd.CommandTimeout = CommandTimeoutSeconds;
        var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>Counts runs in status <c>Running</c> and returns up to five of their ids.</summary>
    public async Task<(long Count, IReadOnlyList<Guid> Sample)> ActiveRunsAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = StorageSql.ActiveRuns(Provider);
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var count = Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
        var sample = new List<Guid>(5);
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sample.Add(reader.GetGuid(0));
        return (count, sample);
    }

    /// <summary>
    /// Reads the next batch after <paramref name="after"/> (the previous batch's last row, or
    /// <see langword="null"/> for the first batch), in key order.
    /// </summary>
    /// <returns>At most <paramref name="take"/> rows; fewer when the text budget is reached. Empty at the end.</returns>
    public async Task<List<object?[]>> ReadBatchAsync(
        StorageTable table, DateTimeOffset cutoff, object?[]? after, int take, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = StorageSql.SelectBatch(Provider, table, after is not null);
        cmd.CommandTimeout = CommandTimeoutSeconds;
        AddParameter(cmd, "@take", take);
        if (table.ExpiryOrdinal >= 0)
            AddParameter(cmd, "@cutoff", cutoff);
        if (after is not null)
        {
            for (var k = 0; k < table.KeyOrdinals.Length; k++)
                AddParameter(cmd, "@k" + k, after[table.KeyOrdinals[k]]!);
        }

        var rows = new List<object?[]>(Math.Min(take, 4096));
        long chars = 0;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object?[table.Columns.Length];
            for (var i = 0; i < row.Length; i++)
            {
                var value = ReadValue(reader, i, table.Columns[i].Kind);
                if (value is string s)
                    chars += s.Length;
                row[i] = value;
            }

            rows.Add(row);
            if (chars >= MaxBatchChars)
                break;
        }

        return rows;
    }

    /// <summary>Reads column <paramref name="ordinal"/> as the CLR type of <paramref name="kind"/>.</summary>
    internal static object? ReadValue(DbDataReader reader, int ordinal, StorageColumnKind kind)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return kind switch
        {
            StorageColumnKind.Guid => reader.GetGuid(ordinal),
            StorageColumnKind.String => reader.GetString(ordinal),
            StorageColumnKind.Boolean => reader.GetBoolean(ordinal),
            StorageColumnKind.Int32 => reader.GetInt32(ordinal),
            StorageColumnKind.Int64 => reader.GetInt64(ordinal),
            StorageColumnKind.Double => reader.GetDouble(ordinal),
            StorageColumnKind.DateTimeOffset => reader.GetFieldValue<DateTimeOffset>(ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>Adds a typed parameter.</summary>
    protected virtual void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
    }
}
