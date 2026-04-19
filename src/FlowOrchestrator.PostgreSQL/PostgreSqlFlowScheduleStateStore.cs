using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// PostgreSQL implementation of <see cref="IFlowScheduleStateStore"/> that persists cron overrides
/// and pause state in the <c>flow_schedule_states</c> table using upsert semantics.
/// </summary>
public sealed class PostgreSqlFlowScheduleStateStore : IFlowScheduleStateStore
{
    private readonly string _connectionString;

    /// <summary>Initialises the store with the given Npgsql connection string.</summary>
    /// <param name="connectionString">PostgreSQL connection string for the FlowOrchestrator database.</param>
    public PostgreSqlFlowScheduleStateStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<FlowScheduleState?> GetAsync(string jobId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<FlowScheduleState>(
            """
            SELECT
                job_id AS "JobId",
                flow_id AS "FlowId",
                flow_name AS "FlowName",
                trigger_key AS "TriggerKey",
                is_paused AS "IsPaused",
                cron_override AS "CronOverride",
                updated_at_utc AS "UpdatedAtUtc"
            FROM flow_schedule_states
            WHERE job_id = @JobId
            """,
            new { JobId = jobId });
    }

    public async Task<IReadOnlyList<FlowScheduleState>> GetAllAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowScheduleState>(
            """
            SELECT
                job_id AS "JobId",
                flow_id AS "FlowId",
                flow_name AS "FlowName",
                trigger_key AS "TriggerKey",
                is_paused AS "IsPaused",
                cron_override AS "CronOverride",
                updated_at_utc AS "UpdatedAtUtc"
            FROM flow_schedule_states
            ORDER BY job_id
            """);
        return rows.AsList();
    }

    public async Task SaveAsync(FlowScheduleState state)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO flow_schedule_states
                (job_id, flow_id, flow_name, trigger_key, is_paused, cron_override, updated_at_utc)
            VALUES
                (@JobId, @FlowId, @FlowName, @TriggerKey, @IsPaused, @CronOverride, NOW())
            ON CONFLICT (job_id) DO UPDATE SET
                flow_id = EXCLUDED.flow_id,
                flow_name = EXCLUDED.flow_name,
                trigger_key = EXCLUDED.trigger_key,
                is_paused = EXCLUDED.is_paused,
                cron_override = EXCLUDED.cron_override,
                updated_at_utc = NOW()
            """,
            state);
    }

    public async Task DeleteAsync(string jobId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM flow_schedule_states WHERE job_id = @JobId",
            new { JobId = jobId });
    }
}

