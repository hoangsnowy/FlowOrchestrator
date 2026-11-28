using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Dapper-based SQL Server implementation of <see cref="IFlowServerStore"/>, over the <c>FlowServers</c> table
/// created by <see cref="FlowOrchestratorSqlMigrator"/>.
/// </summary>
/// <remarks>
/// Every timestamp is <c>SYSDATETIMEOFFSET()</c> on the database server, so replicas with skewed clocks are aged
/// against one clock. The table holds one row per live process: no index beyond the primary key is needed.
/// </remarks>
internal sealed class SqlFlowServerStore : IFlowServerStore
{
    private const string Columns = """
        ServerId, MachineName, ProcessId, Runtime, Version, StartedAt, LastHeartbeatAt, ActiveStepCount, FlowCount
        """;

    private readonly string _connectionString;

    /// <summary>Creates the store.</summary>
    /// <param name="connectionString">The FlowOrchestrator database.</param>
    public SqlFlowServerStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task UpsertServerAsync(FlowServerRecord server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(server.ServerId);
        await using var conn = new SqlConnection(_connectionString);
        // HOLDLOCK, UPDLOCK as in SqlFlowSignalStore: a concurrent first write of the same id cannot race into a PK violation.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            MERGE [FlowServers] WITH (HOLDLOCK, UPDLOCK) AS target
            USING (SELECT @ServerId AS ServerId) AS source
            ON target.ServerId = source.ServerId
            WHEN MATCHED THEN
                UPDATE SET MachineName = @MachineName, ProcessId = @ProcessId, Runtime = @Runtime, Version = @Version,
                           LastHeartbeatAt = SYSDATETIMEOFFSET(), ActiveStepCount = @ActiveStepCount, FlowCount = @FlowCount
            WHEN NOT MATCHED THEN
                INSERT (ServerId, MachineName, ProcessId, Runtime, Version, StartedAt, LastHeartbeatAt, ActiveStepCount, FlowCount)
                VALUES (@ServerId, @MachineName, @ProcessId, @Runtime, @Version, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), @ActiveStepCount, @FlowCount);
            """,
            new
            {
                server.ServerId,
                server.MachineName,
                server.ProcessId,
                server.Runtime,
                server.Version,
                server.ActiveStepCount,
                server.FlowCount,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FlowServerRecord>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowServerRecord>(new CommandDefinition(
            $"SELECT {Columns} FROM [FlowServers] ORDER BY LastHeartbeatAt DESC, ServerId",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        await using var conn = new SqlConnection(_connectionString);
        var removed = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM [FlowServers] WHERE ServerId = @ServerId",
            new { ServerId = serverId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return removed > 0;
    }

    /// <inheritdoc/>
    public async Task<int> PurgeServersAsync(TimeSpan notSeenFor, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(notSeenFor, TimeSpan.Zero);
        await using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM [FlowServers] WHERE LastHeartbeatAt < DATEADD(SECOND, -@Seconds, SYSDATETIMEOFFSET())",
            new { Seconds = WholeSeconds(notSeenFor) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The age in whole seconds for <c>DATEADD</c>, rounded up and capped at <see cref="int.MaxValue"/>.</summary>
    /// <param name="age">A positive age.</param>
    /// <returns>At least 1.</returns>
    internal static int WholeSeconds(TimeSpan age)
        => (int)Math.Clamp(Math.Ceiling(age.TotalSeconds), 1d, int.MaxValue);
}
