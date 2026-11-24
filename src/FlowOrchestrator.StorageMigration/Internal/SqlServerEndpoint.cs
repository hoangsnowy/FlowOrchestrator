using System.Data;
using System.Data.Common;
using FlowOrchestrator.SqlServer;
using FlowOrchestrator.StorageMigration.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.StorageMigration.Internal;

/// <summary>SQL Server side of a copy: reads with ADO.NET, writes with <see cref="SqlBulkCopy"/>.</summary>
internal sealed class SqlServerEndpoint : StorageEndpoint
{
    private readonly SqlConnectionStringBuilder _builder;

    public SqlServerEndpoint(string connectionString, int commandTimeoutSeconds)
        : base(FlowStorageProvider.SqlServer, connectionString, commandTimeoutSeconds)
    {
        _builder = new SqlConnectionStringBuilder(connectionString);
    }

    /// <inheritdoc/>
    public override string Identity => NormalizeIdentity(_builder.DataSource, _builder.InitialCatalog);

    /// <inheritdoc/>
    public override string Display => $"SQL Server {_builder.DataSource} / {(_builder.InitialCatalog is { Length: > 0 } db ? db : "(default database)")}";

    /// <summary>Lower-cased server (without a <c>tcp:</c> prefix or the default port) and database.</summary>
    internal static string NormalizeIdentity(string dataSource, string database)
    {
        var server = dataSource.Trim().ToLowerInvariant();
        if (server.StartsWith("tcp:", StringComparison.Ordinal))
            server = server[4..];
        if (server.EndsWith(",1433", StringComparison.Ordinal))
            server = server[..^5];
        if (server is "." or "(local)")
            server = "localhost";
        return "sqlserver://" + server + "/" + database.Trim().ToLowerInvariant();
    }

    /// <inheritdoc/>
    protected override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    /// <inheritdoc/>
    protected override void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = new SqlParameter { ParameterName = name };
        switch (value)
        {
            case Guid g:
                p.SqlDbType = SqlDbType.UniqueIdentifier;
                p.Value = g;
                break;
            case string s:
                p.SqlDbType = SqlDbType.NVarChar;
                p.Size = s.Length <= 4000 ? 4000 : -1;
                p.Value = s;
                break;
            case DateTimeOffset dto:
                p.SqlDbType = SqlDbType.DateTimeOffset;
                p.Value = dto;
                break;
            case int i:
                p.SqlDbType = SqlDbType.Int;
                p.Value = i;
                break;
            case long l:
                p.SqlDbType = SqlDbType.BigInt;
                p.Value = l;
                break;
            default:
                p.Value = value;
                break;
        }

        cmd.Parameters.Add(p);
    }

    /// <inheritdoc/>
    public override async Task RunMigratorAsync(ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        using var migrator = new FlowOrchestratorSqlMigrator(
            ConnectionString, loggerFactory.CreateLogger<FlowOrchestratorSqlMigrator>(), runSearchIndexEnabled: false);
        await migrator.StartAsync(cancellationToken).ConfigureAwait(false);
        // Developer / Enterprise / Azure build the run-search indexes online in the background; wait, so the
        // bulk load (TABLOCK) does not contend with an index build on the same table.
        await migrator.BackgroundIndexBuild.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<ITableWriter> BeginTableAsync(StorageTable table, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new Writer(table, connection, transaction, CommandTimeoutSeconds);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resets the run-search token index on the target (when its tables exist) so the next host start with
    /// <c>UseRunSearchIndex()</c> back-fills the copied runs. Without this, an index whose back-fill already
    /// completed on the empty database would never index the copied history.
    /// </summary>
    /// <returns><see langword="true"/> when the token index existed and was reset.</returns>
    public async Task<bool> ResetRunSearchTokenIndexAsync(SchemaSnapshot schema, CancellationToken cancellationToken)
    {
        if (!schema.HasTable("FlowRunSearchIndexState") || !schema.HasTable("FlowRunSearchTokens"))
            return false;

        await ExecuteAsync("""
            DELETE FROM [FlowRunSearchTokens];
            UPDATE [FlowRunSearchIndexState]
               SET [BackfillAfterStartedAt] = NULL, [BackfillAfterId] = NULL, [BackfillCompletedAt] = NULL
             WHERE [Id] = 1;
            """, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed class Writer : ITableWriter
    {
        private readonly StorageTable _table;
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;
        private readonly int _timeout;
        private bool _committed;

        public Writer(StorageTable table, SqlConnection connection, SqlTransaction transaction, int timeout)
        {
            _table = table;
            _connection = connection;
            _transaction = transaction;
            _timeout = timeout;
        }

        public async Task WriteAsync(IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
        {
            // KeepIdentity: ids are copied as they are. KeepNulls: a NULL stays NULL instead of taking the
            // column default. TableLock: the target is a fresh database nobody else writes to; a bulk-update
            // lock is what makes the load minimally logged where the recovery model allows it.
            using var bulk = new SqlBulkCopy(
                _connection,
                SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock,
                _transaction)
            {
                DestinationTableName = "[" + _table.SqlName + "]",
                BulkCopyTimeout = _timeout,
                BatchSize = 0,
            };
            for (var i = 0; i < _table.Columns.Length; i++)
                bulk.ColumnMappings.Add(i, _table.Columns[i].SqlName);

            await using var reader = new RowBatchDataReader(_table, rows);
            await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        public async Task CommitAsync(bool reseedIdentity, CancellationToken cancellationToken)
        {
            if (reseedIdentity && _table.IdentityOrdinal >= 0)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = _transaction;
                cmd.CommandText = StorageSql.ReseedIdentity(FlowStorageProvider.SqlServer, _table);
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
                    // Already rolled back by the server (e.g. a failed bulk load dooms the transaction).
                }
                catch (SqlException)
                {
                    // Connection broken: the server rolls back on its own.
                }
            }

            await _transaction.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
