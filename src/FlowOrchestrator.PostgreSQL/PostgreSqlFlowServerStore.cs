using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// Dapper-based PostgreSQL implementation of <see cref="IFlowServerStore"/>, over the <c>flow_servers</c> table
/// created by <see cref="PostgreSqlFlowOrchestratorMigrator"/>.
/// </summary>
/// <remarks>
/// Every timestamp is <c>NOW()</c> on the database server, so replicas with skewed clocks are aged against one
/// clock. The table holds one row per live process: no index beyond the primary key is needed.
/// </remarks>
internal sealed class PostgreSqlFlowServerStore : IFlowServerStore
{
    private const string Columns = """
        server_id AS "ServerId", machine_name AS "MachineName", process_id AS "ProcessId", runtime AS "Runtime",
        version AS "Version", started_at AS "StartedAt", last_heartbeat_at AS "LastHeartbeatAt",
        active_step_count AS "ActiveStepCount", flow_count AS "FlowCount"
        """;

    private readonly string _connectionString;

    /// <summary>Creates the store.</summary>
    /// <param name="connectionString">The FlowOrchestrator database.</param>
    public PostgreSqlFlowServerStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task UpsertServerAsync(FlowServerRecord server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(server.ServerId);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO flow_servers (server_id, machine_name, process_id, runtime, version, started_at, last_heartbeat_at, active_step_count, flow_count)
            VALUES (@ServerId, @MachineName, @ProcessId, @Runtime, @Version, NOW(), NOW(), @ActiveStepCount, @FlowCount)
            ON CONFLICT (server_id) DO UPDATE SET
                machine_name      = EXCLUDED.machine_name,
                process_id        = EXCLUDED.process_id,
                runtime           = EXCLUDED.runtime,
                version           = EXCLUDED.version,
                last_heartbeat_at = NOW(),
                active_step_count = EXCLUDED.active_step_count,
                flow_count        = EXCLUDED.flow_count
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
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowServerRecord>(new CommandDefinition(
            $"SELECT {Columns}, now() AS \"ObservedAt\" FROM flow_servers ORDER BY last_heartbeat_at DESC, server_id",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        await using var conn = new NpgsqlConnection(_connectionString);
        var removed = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM flow_servers WHERE server_id = @ServerId",
            new { ServerId = serverId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return removed > 0;
    }

    /// <inheritdoc/>
    public async Task<int> PurgeServersAsync(TimeSpan notSeenFor, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(notSeenFor, TimeSpan.Zero);
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM flow_servers WHERE last_heartbeat_at < NOW() - @NotSeenFor",
            new { NotSeenFor = notSeenFor },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
