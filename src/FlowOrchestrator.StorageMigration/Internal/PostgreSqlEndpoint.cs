using System.Data.Common;
using FlowOrchestrator.PostgreSQL;
using FlowOrchestrator.StorageMigration.Schema;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace FlowOrchestrator.StorageMigration.Internal;

/// <summary>PostgreSQL side of a copy: reads with ADO.NET, writes with binary <c>COPY ... FROM STDIN</c>.</summary>
internal sealed class PostgreSqlEndpoint : StorageEndpoint
{
    private readonly NpgsqlConnectionStringBuilder _builder;

    public PostgreSqlEndpoint(string connectionString, int commandTimeoutSeconds)
        : base(FlowStorageProvider.PostgreSql, connectionString, commandTimeoutSeconds)
    {
        _builder = new NpgsqlConnectionStringBuilder(connectionString);
    }

    /// <inheritdoc/>
    public override string Identity =>
        "postgresql://" + (_builder.Host ?? "localhost").Trim().ToLowerInvariant() + ":" + _builder.Port + "/"
        + (_builder.Database ?? _builder.Username ?? string.Empty).Trim().ToLowerInvariant();

    /// <inheritdoc/>
    public override string Display => $"PostgreSQL {_builder.Host}:{_builder.Port} / {_builder.Database ?? "(default database)"}";

    /// <inheritdoc/>
    protected override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    /// <inheritdoc/>
    public override async Task RunMigratorAsync(ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        using var migrator = new PostgreSqlFlowOrchestratorMigrator(
            ConnectionString, loggerFactory.CreateLogger<PostgreSqlFlowOrchestratorMigrator>());
        await migrator.StartAsync(cancellationToken).ConfigureAwait(false);
        // The run-search trigram index is built CONCURRENTLY in the background; on the still-empty table it
        // is instant, and waiting keeps it from racing the COPY.
        await migrator.BackgroundIndexBuild.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<ITableWriter> BeginTableAsync(StorageTable table, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new Writer(table, connection, transaction, CommandTimeoutSeconds);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Refreshes planner statistics for the copied tables. A bulk load leaves them empty, and without them the
    /// run-search expression index is estimated at ~20 matches for any term.
    /// </summary>
    public Task AnalyzeAsync(IEnumerable<StorageTable> tables, CancellationToken cancellationToken)
    {
        var names = string.Join(", ", tables.Select(t => StorageSql.Quote(FlowStorageProvider.PostgreSql, t.PgName)));
        return names.Length == 0 ? Task.CompletedTask : ExecuteAsync("ANALYZE " + names, cancellationToken);
    }

    /// <summary>The Npgsql wire type of a column (binary COPY needs it exact).</summary>
    internal static NpgsqlDbType DbTypeOf(StorageColumn column) => column.Kind switch
    {
        StorageColumnKind.Guid => NpgsqlDbType.Uuid,
        StorageColumnKind.String => column.MaxLength is null ? NpgsqlDbType.Text : NpgsqlDbType.Varchar,
        StorageColumnKind.Boolean => NpgsqlDbType.Boolean,
        StorageColumnKind.Int32 => NpgsqlDbType.Integer,
        StorageColumnKind.Int64 => NpgsqlDbType.Bigint,
        StorageColumnKind.Double => NpgsqlDbType.Double,
        StorageColumnKind.DateTimeOffset => NpgsqlDbType.TimestampTz,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.Kind, null),
    };

    private sealed class Writer : ITableWriter
    {
        private readonly StorageTable _table;
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private readonly int _timeout;
        private readonly NpgsqlDbType[] _types;
        private readonly string _copySql;
        private bool _committed;

        public Writer(StorageTable table, NpgsqlConnection connection, NpgsqlTransaction transaction, int timeout)
        {
            _table = table;
            _connection = connection;
            _transaction = transaction;
            _timeout = timeout;
            _types = new NpgsqlDbType[table.Columns.Length];
            for (var i = 0; i < _types.Length; i++)
                _types[i] = DbTypeOf(table.Columns[i]);
            _copySql = "COPY " + StorageSql.Quote(FlowStorageProvider.PostgreSql, table.PgName)
                + " (" + StorageSql.ColumnList(FlowStorageProvider.PostgreSql, table) + ") FROM STDIN (FORMAT BINARY)";
        }

        public async Task WriteAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
        {
            await using var importer = await _connection.BeginBinaryImportAsync(_copySql, cancellationToken).ConfigureAwait(false);
            if (_timeout > 0)
                importer.Timeout = TimeSpan.FromSeconds(_timeout);
            foreach (var row in rows)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < row.Length; i++)
                {
                    if (row[i] is { } value)
                        await importer.WriteAsync(value, _types[i], cancellationToken).ConfigureAwait(false);
                    else
                        await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task CommitAsync(bool reseedIdentity, CancellationToken cancellationToken)
        {
            if (reseedIdentity && _table.IdentityOrdinal >= 0)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = _transaction;
                cmd.CommandText = StorageSql.ReseedIdentity(FlowStorageProvider.PostgreSql, _table);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                try
                {
                    await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Already completed.
                }
                catch (NpgsqlException)
                {
                    // Connection broken: the server rolls back on its own.
                }
            }

            await _transaction.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
